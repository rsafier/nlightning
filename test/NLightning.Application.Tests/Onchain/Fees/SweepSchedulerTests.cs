using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Fees;

using Application.Onchain.Fees;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Resolvers.Local;
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