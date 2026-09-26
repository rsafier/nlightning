using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Fees;

using Application.Onchain.Fees;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Resolvers.Local;
using Resolvers.Revoked;
using static Resolvers.Local.LocalCommitResolutionHarness;

/// <summary>
/// BOLT 5 plan O6-T1 (§3.7, NL-317): <see cref="SweepScheduler"/> replaces our unconfirmed sweeps with a higher fee on
/// schedule, re-signed over the same inputs (checked by script execution against the outputs they spend), persisted
/// with <c>ReplacesTxId</c> and the output row moved to the replacement; and retires a transaction that can no longer
/// confirm.
/// </summary>
public sealed class SweepSchedulerTests
{
    private const uint SweepTarget = 6;

    [Fact]
    public async Task Given_NotConfirmedAfterInterval_Then_ReplacedWithHigherFee()
    {
        // Arrange: our to_local swept at the CSV, then the sweep stays out of every block
        using var harness = new LocalCommitResolutionHarness();
        var scheduler = CreateScheduler(harness);
        await harness.ResolveAsync();
        var toLocal = harness.VoutOf(OutputDescriptorKind.DelayedToLocal);
        await harness.MineToAsync(CloseHeight + Csv - 1);
        var original = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep));
        var originalRow = harness.Broadcasts[new TxId(original.GetHash().ToBytes())];
        harness.HoldMempool = true;

        // Act: rounds before the sweep target is reached
        for (var i = 1; i < SweepTarget; i++)
        {
            await harness.MineAsync();
            Assert.Empty(await PlanAsync(harness, scheduler));
        }

        // Act: the round at the sweep target
        await harness.MineAsync();
        var actions = await PlanAsync(harness, scheduler);
        await harness.ApplyActionsAsync(actions);

        // Assert: one replacement of the same input, same destination, at a BIP 125 higher fee, valid by script
        var replacementRow = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(originalRow.TransactionId, replacementRow.ReplacesTransactionId);
        Assert.Equal(BroadcastPurpose.Sweep, replacementRow.Purpose);
        Assert.Equal(harness.Height, replacementRow.FirstBroadcastHeight);
        var replacement = Transaction.Load(replacementRow.RawTransaction, Network.Main);
        Assert.Equal(original.Inputs.Select(i => (i.PrevOut, i.Sequence.Value)),
                     replacement.Inputs.Select(i => (i.PrevOut, i.Sequence.Value)));
        Assert.Equal(original.LockTime, replacement.LockTime);
        Assert.Equal(harness.Destination, Assert.Single(replacement.Outputs).ScriptPubKey.ToBytes());
        var amount = OutputDescriptorData.TryDecode(harness.CommitmentRow(toLocal))!.AmountSat;
        var oldFee = amount - (ulong)original.Outputs[0].Value.Satoshi;
        var newFee = amount - (ulong)replacement.Outputs[0].Value.Satoshi;
        Assert.True(newFee >= new SweepFeePolicy().GetReplacementFee(oldFee, replacement.GetVirtualSize()),
                    $"replacement fee {newFee} sat must outbid {oldFee} sat by the BIP 125 rules");
        harness.AssertAllInputsVerify(replacement);

        // Assert: old row replaced, the output row names the replacement, the mempool holds only the replacement
        Assert.Equal(BroadcastState.Replaced, originalRow.State);
        Assert.Equal(replacementRow.TransactionId, harness.CommitmentRow(toLocal).ResolvingTransactionId);
        Assert.Equal(replacement.GetHash(), Assert.Single(harness.Mempool).GetHash());

        // Act: the replacement confirms
        harness.HoldMempool = false;
        await harness.MineAsync();

        // Assert: resolved by the replacement, nothing more to bump
        Assert.Equal(OutputResolutionState.Resolved, harness.CommitmentRow(toLocal).State);
        Assert.Empty(await PlanAsync(harness, scheduler));
    }

    [Fact]
    public async Task Given_HigherEstimateForTheTarget_When_Bumping_Then_TheEstimateIsPaid()
    {
        // Arrange: the fee market moved up while the sweep waited
        using var harness = new LocalCommitResolutionHarness();
        var scheduler = CreateScheduler(harness);
        await harness.ResolveAsync();
        var toLocal = harness.VoutOf(OutputDescriptorKind.DelayedToLocal);
        await harness.MineToAsync(CloseHeight + Csv - 1);
        harness.HoldMempool = true;
        await harness.MineToAsync(harness.Height + SweepTarget);
        harness.FeeEstimatePerKw = 20_000;

        // Act
        var actions = await PlanAsync(harness, scheduler);

        // Assert: the new fee is the estimate times the weight, not just the BIP 125 minimum
        var replacement = Transaction.Load(Assert.Single(actions.OfType<BroadcastAction>()).Transaction.RawTransaction,
                                           Network.Main);
        var amount = OutputDescriptorData.TryDecode(harness.CommitmentRow(toLocal))!.AmountSat;
        var newFee = amount - (ulong)replacement.Outputs[0].Value.Satoshi;
        var rate = newFee * 1000 / (ulong)replacement.GetVirtualSize() / 4;
        Assert.InRange(rate, 19_000UL, 20_500UL);
    }

    [Fact]
    public async Task Given_OriginalMinedAfterItsReplacement_When_Planning_Then_ReplacementIsAbandoned()
    {
        // Arrange: a replacement exists, but a miner took the original
        using var harness = new LocalCommitResolutionHarness();
        var scheduler = CreateScheduler(harness);
        await harness.ResolveAsync();
        var toLocal = harness.VoutOf(OutputDescriptorKind.DelayedToLocal);
        await harness.MineToAsync(CloseHeight + Csv - 1);
        var original = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep));
        harness.HoldMempool = true;
        await harness.MineToAsync(harness.Height + SweepTarget);
        var bump = await PlanAsync(harness, scheduler);
        await harness.ApplyActionsAsync(bump);
        var replacementId = Assert.Single(bump.OfType<BroadcastAction>()).Transaction.TransactionId;
        await harness.MineAsync(original);
        Assert.Equal(OutputResolutionState.Resolved, harness.CommitmentRow(toLocal).State);

        // Act
        var actions = await PlanAsync(harness, scheduler);
        await harness.ApplyActionsAsync(actions);

        // Assert: the replacement can never confirm: abandoned, not bumped again
        Assert.DoesNotContain(actions, a => a is BroadcastAction);
        Assert.Equal(BroadcastState.Abandoned, harness.Broadcasts[replacementId].State);
    }

    [Fact]
    public async Task Given_FeeCannotOutbidWithinTheCap_When_Planning_Then_TheSweepIsKept()
    {
        // Arrange: a sweep target reached but a policy whose cap is below any replacement fee
        using var harness = new LocalCommitResolutionHarness();
        var scheduler = CreateScheduler(harness, new SweepFeePolicyOptions
        {
            SweepConfTarget = SweepTarget,
            SweepMaxFeePerMille = 0
        });
        await harness.ResolveAsync();
        await harness.MineToAsync(CloseHeight + Csv - 1);
        harness.HoldMempool = true;
        await harness.MineToAsync(harness.Height + SweepTarget);

        // Act
        var actions = await PlanAsync(harness, scheduler);

        // Assert
        Assert.Empty(actions);
        Assert.All(harness.Broadcasts.Values, b => Assert.Equal(BroadcastState.Pending, b.State));
    }

    [Fact]
    public async Task Given_UnconfirmedPenaltyBeforeItsDeadline_When_IntervalPassed_Then_ReplacedWithTheRevocationKey()
    {
        // Arrange (O6-T1 for penalties): a breach with an HTLC each way; the penalties go out and stay unconfirmed
        using var kit = new RevokedBreachKit();
        var pair = kit.Pair;
        pair.Add(pair.Bob, 50_000_000, RealSigningCommitmentPair.Preimage(0xB1), 600);
        pair.Add(pair.Alice, 40_000_000, RealSigningCommitmentPair.Preimage(0xA1), 650);
        pair.Settle(pair.Bob);
        kit.CaptureRevokedState();
        pair.UpdateFee(3_000);
        pair.Settle(pair.Alice);
        kit.Breach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);

        // The penalty whose outputs' deadlines are all still ahead (the urgent one went out alone)
        var height = RevokedBreachKit.SpentAtHeight + 1 + s_policy.RbfIntervalBlocks;
        var penalty = kit.Broadcasts.Values.First(b =>
        {
            var deadlines = kit.Rows.Where(r => r.ResolvingTransactionId == b.TransactionId)
                               .Select(r => r.DeadlineHeight).ToList();
            return b.Purpose == BroadcastPurpose.Penalty && deadlines.Count > 0
                && deadlines.All(d => d is null || d > height);
        });
        var secret = kit.DataSource.Context!.PerCommitmentSecret;
        var scheduler = new SweepScheduler(CreateFeeService(RevokedBreachKit.FeeratePerKw).Object, kit.Victim.Signer,
                                           NullLogger<SweepScheduler>.Instance, new SweepFeePolicy(s_policy),
                                           CreateShachain(secret).Object);

        // Act
        var actions = await scheduler.PlanAsync(kit.Close, kit.Rows.ToList(), height, CreateUnitOfWork(kit),
                                                TestContext.Current.CancellationToken);
        kit.Apply(actions);

        // Assert: the same revoked outputs, re-signed with the revocation key (script-valid), at a BIP 125 higher fee
        var replacement = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(BroadcastPurpose.Penalty, replacement.Purpose);
        Assert.Equal(penalty.TransactionId, replacement.ReplacesTransactionId);
        var oldTx = kit.LoadBroadcast(penalty.TransactionId);
        var newTx = kit.LoadBroadcast(replacement.TransactionId);
        Assert.Equal(oldTx.Inputs.Select(i => i.PrevOut), newTx.Inputs.Select(i => i.PrevOut));
        kit.AssertVerifies(replacement.TransactionId);
        var inputValue = kit.Rows.Where(r => newTx.Inputs.Any(i => i.PrevOut.Hash == new uint256((byte[])r.TransactionId)
                                                               && i.PrevOut.N == r.OutputIndex))
                            .Sum(r => (long)OutputDescriptorData.TryDecode(r)!.AmountSat);
        var oldFee = inputValue - oldTx.Outputs.Sum(o => o.Value.Satoshi);
        var newFee = inputValue - newTx.Outputs.Sum(o => o.Value.Satoshi);
        Assert.True(newFee >= (long)s_policy.RbfFeeMultiplierPerMille * oldFee / 1000,
                    $"penalty fee {oldFee} -> {newFee}");
        Assert.All(kit.Rows.Where(r => r.ResolvingTransactionId == penalty.TransactionId), _ => Assert.Fail("moved"));
        Assert.Contains(actions, a => a is StageWriteAction { Description: var d } && d.StartsWith("replaced"));
    }

    private static readonly SweepFeePolicyOptions s_policy = new();

    private static Mock<IFeeService> CreateFeeService(uint feeratePerKw)
    {
        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(feeratePerKw));
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(feeratePerKw));
        return feeService;
    }

    /// <summary>A shachain that derives the peer's secret of the revoked commitment.</summary>
    private static Mock<ISecretStorageServiceFactory> CreateShachain(Secret secret)
    {
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        var factory = new Mock<ISecretStorageServiceFactory>();
        factory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        return factory;
    }

    private static IUnitOfWork CreateUnitOfWork(RevokedBreachKit kit)
    {
        var broadcasts = new Mock<IBroadcastTransactionDbRepository>();
        broadcasts.Setup(r => r.GetByChannelIdAsync(It.IsAny<Domain.Channels.ValueObjects.ChannelId>()))
                  .ReturnsAsync(() => kit.Broadcasts.Values.ToList());
        var shachain = new Mock<IRemoteShachainDbRepository>();
        shachain.Setup(r => r.GetByChannelIdAsync(It.IsAny<Domain.Channels.ValueObjects.ChannelId>()))
                .ReturnsAsync(Array.Empty<ShachainEntry>());
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(broadcasts.Object);
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(shachain.Object);
        return unitOfWork.Object;
    }

    private static SweepScheduler CreateScheduler(LocalCommitResolutionHarness harness,
                                                  SweepFeePolicyOptions? options = null) =>
        new(harness.GetService<IFeeService>(), harness.GetService<ILightningSigner>(),
            NullLogger<SweepScheduler>.Instance,
            new SweepFeePolicy(options ?? new SweepFeePolicyOptions { SweepConfTarget = SweepTarget }));

    private static Task<IReadOnlyList<OutputResolverAction>> PlanAsync(LocalCommitResolutionHarness harness,
                                                                      SweepScheduler scheduler) =>
        scheduler.PlanAsync(harness.Close, harness.Rows.Values.ToList(), harness.Height,
                            harness.CreateUnitOfWorkForTests(), TestContext.Current.CancellationToken);
}