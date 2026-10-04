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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_NotConfirmedAfterInterval_Then_ReplacedWithHigherFee(bool simpleTaproot)
    {
        // Arrange: our to_local swept at the CSV, then the sweep stays out of every block (a simple taproot channel
        // re-signs its script-path input with BIP 340 over every spent output, NL-877 T4)
        using var harness = new LocalCommitResolutionHarness(simpleTaproot: simpleTaproot);
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
        Assert.Equal((long)newFee, replacementRow.Fee?.Satoshi); // NL-604
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
    public async Task Given_OurTaprootSecondLevelSweepUnconfirmed_When_Bumped_Then_ReSignedByItsDelayLeafAndValid()
    {
        // Arrange (NL-966): a simple taproot HTLC-success confirms, its P2TR output is swept by the delay leaf after the
        // CSV, then the sweep stays out of every block
        var wallet = new AnchorTestWallet(true, 60_000);
        var preimage = RealSigningCommitmentPair.Preimage(2);
        using var harness = new LocalCommitResolutionHarness(pair =>
        {
            var id = pair.Add(pair.Bob, 30_000_000, preimage, 1_020);
            pair.Settle(pair.Bob);
            pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, preimage,
                                                                     new Infrastructure.Crypto.Hashes.Sha256()));
        }, feeInputProvider: wallet, simpleTaproot: true);
        foreach (var funding in wallet.FundingTransactions)
            harness.AddKnownTransaction(funding);
        var scheduler = CreateScheduler(harness);
        await harness.ResolveAsync();
        var success = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        await harness.MineAsync();
        await harness.MineToAsync(harness.Height + Csv - 1);
        var original = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep),
                                     t => t.Inputs[0].PrevOut == new OutPoint(success, 0));
        harness.HoldMempool = true;

        // Act: the round at the sweep target
        await harness.MineToAsync(harness.Height + SweepTarget);
        var actions = await PlanAsync(harness, scheduler);
        await harness.ApplyActionsAsync(actions);

        // Assert: the second-level sweep replaced over the same input, the BIP 340 signature made again, valid
        var replacementRow = Assert.Single(actions.OfType<BroadcastAction>(),
                                           a => a.Transaction.ReplacesTransactionId
                                             == new TxId(original.GetHash().ToBytes())).Transaction;
        var replacement = Transaction.Load(replacementRow.RawTransaction, Network.Main);
        Assert.Equal(original.Inputs[0].PrevOut, Assert.Single(replacement.Inputs).PrevOut);
        Assert.Equal((uint)Csv, replacement.Inputs[0].Sequence.Value);
        Assert.Equal(3, replacement.Inputs[0].WitScript.PushCount);
        Assert.NotEqual(original.Inputs[0].WitScript[0], replacement.Inputs[0].WitScript[0]);
        Assert.True(replacement.Outputs[0].Value < original.Outputs[0].Value);
        harness.AssertAllInputsVerify(replacement);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_UnconfirmedPenaltyBeforeItsDeadline_When_IntervalPassed_Then_ReplacedWithTheRevocationKey(
        bool simpleTaproot)
    {
        // Arrange (O6-T1 for penalties): a breach with an HTLC each way; the penalties go out and stay unconfirmed (a
        // simple taproot channel re-signs its HTLC inputs by the revocation key path and to_local by its leaf, NL-966)
        using var kit = new RevokedBreachKit(simpleTaproot: simpleTaproot);
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
        await kit.ApplyAsync(actions);

        // Assert: the same revoked outputs, re-signed with the revocation key (script-valid), at a BIP 125 higher fee
        // (on the taproot channel the isolated penalty of our offered HTLC, O7-T3, is due for its bump too)
        var replacements = actions.OfType<BroadcastAction>().Select(a => a.Transaction).ToList();
        Assert.Equal(simpleTaproot ? 2 : 1, replacements.Count);
        foreach (var other in replacements)
            kit.AssertVerifies(other.TransactionId);
        var replacement = Assert.Single(replacements, r => r.ReplacesTransactionId == penalty.TransactionId);
        Assert.Equal(BroadcastPurpose.Penalty, replacement.Purpose);
        Assert.Equal(penalty.TransactionId, replacement.ReplacesTransactionId);
        var oldTx = kit.LoadBroadcast(penalty.TransactionId);
        var newTx = kit.LoadBroadcast(replacement.TransactionId);
        Assert.Equal(oldTx.Inputs.Select(i => i.PrevOut), newTx.Inputs.Select(i => i.PrevOut));
        Assert.Equal(oldTx.Inputs.Select(i => i.WitScript.PushCount), newTx.Inputs.Select(i => i.WitScript.PushCount));
        if (simpleTaproot)
            Assert.Contains(newTx.Inputs, i => i.WitScript.PushCount == 1);
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

    [Fact]
    public async Task Given_TaprootRevokedHtlcRowsRecordedWithoutALeaf_When_AResolverRoundRuns_Then_ThePenaltyIsBumped()
    {
        // Arrange (NL-1051): the watcher of a t02 build wrote the revoked commitment's HTLC rows without a leaf and
        // control block; a t03 build penalizes those outputs by key path from the fresh map, so its fee bump needs them
        using var kit = new RevokedBreachKit(simpleTaproot: true);
        var pair = kit.Pair;
        pair.Add(pair.Bob, 50_000_000, RealSigningCommitmentPair.Preimage(0xB1), 600);
        pair.Add(pair.Alice, 40_000_000, RealSigningCommitmentPair.Preimage(0xA1), 650);
        pair.Settle(pair.Bob);
        kit.CaptureRevokedState();
        pair.UpdateFee(3_000);
        pair.Settle(pair.Alice);
        kit.Breach();
        await kit.RunAsync(RevokedBreachKit.SpentAtHeight + 1);
        for (var i = 0; i < kit.Rows.Count; i++)
        {
            if (kit.Rows[i].Descriptor != OutputDescriptorKind.RevokedHtlc)
                continue;

            var data = OutputDescriptorData.TryDecode(kit.Rows[i])!;
            kit.Rows[i] = kit.Rows[i] with
            {
                DescriptorData = (data with { WitnessScript = null, TaprootControlBlock = null }).Encode()
            };
        }

        var height = RevokedBreachKit.SpentAtHeight + 1 + s_policy.RbfIntervalBlocks;
        var htlcPenalties = kit.Broadcasts.Values
                               .Where(b => b.Purpose == BroadcastPurpose.Penalty
                                        && kit.Rows.Any(r => r.ResolvingTransactionId == b.TransactionId
                                                          && r.Descriptor == OutputDescriptorKind.RevokedHtlc))
                               .Select(b => b.TransactionId)
                               .ToList();
        Assert.NotEmpty(htlcPenalties);
        var secret = kit.DataSource.Context!.PerCommitmentSecret;
        var scheduler = new SweepScheduler(CreateFeeService(RevokedBreachKit.FeeratePerKw).Object, kit.Victim.Signer,
                                           NullLogger<SweepScheduler>.Instance, new SweepFeePolicy(s_policy),
                                           CreateShachain(secret).Object);

        // Act: a resolver round of the upgraded build, then the scheduler
        await kit.RunAsync(height);
        var actions = await scheduler.PlanAsync(kit.Close, kit.Rows.ToList(), height, CreateUnitOfWork(kit),
                                                TestContext.Current.CancellationToken);

        // Assert: every penalty of an HTLC output is replaced, re-signed by the revocation key path (script-valid)
        var replacements = actions.OfType<BroadcastAction>().Select(a => a.Transaction).ToList();
        await kit.ApplyAsync(actions);
        foreach (var penalty in htlcPenalties)
        {
            var replacement = Assert.Single(replacements, r => r.ReplacesTransactionId == penalty);
            kit.AssertVerifies(replacement.TransactionId);
        }
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