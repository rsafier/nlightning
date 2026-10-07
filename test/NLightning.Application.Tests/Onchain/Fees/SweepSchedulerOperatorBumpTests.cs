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
/// The operator's fee bump of a sweep (LND's walletrpc <c>BumpFee</c> on a resolution output, NL-1186): a fresh request
/// replaces the pending sweep at once at its starting rate and within its budget, then the regular schedule resumes.
/// </summary>
public sealed class SweepSchedulerOperatorBumpTests
{
    [Fact]
    public async Task Given_APendingSweep_When_TheOperatorAsksForAHigherRate_Then_ReplacedAtOnceAndOnlyOnce()
    {
        // Arrange: our to_local swept at the CSV, held out of blocks
        using var harness = new LocalCommitResolutionHarness();
        var bumps = new OperatorFeeBumps();
        var scheduler = CreateScheduler(harness, bumps);
        await harness.ResolveAsync();
        var toLocal = harness.VoutOf(OutputDescriptorKind.DelayedToLocal);
        await harness.MineToAsync(CloseHeight + Csv - 1);
        var original = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep));
        harness.HoldMempool = true;
        await harness.MineAsync();
        var row = harness.CommitmentRow(toLocal);

        // Act: the operator's request, one block after the sweep (long before the sweep target)
        bumps.SetOutput(row.TransactionId, row.OutputIndex,
                        new OperatorFeeBumpRequest(20_000, null, null, null, false));
        var actions = await PlanAsync(harness, scheduler);
        await harness.ApplyActionsAsync(actions);
        await harness.MineAsync();
        var again = await PlanAsync(harness, scheduler);

        // Assert: replaced at the operator's rate, the request applied, no second replacement at the next block
        var replacementRow = Assert.Single(actions.OfType<BroadcastAction>()).Transaction;
        Assert.Equal(new TxId(original.GetHash().ToBytes()), replacementRow.ReplacesTransactionId);
        var replacement = Transaction.Load(replacementRow.RawTransaction, Network.Main);
        var amount = OutputDescriptorData.TryDecode(harness.CommitmentRow(toLocal))!.AmountSat;
        var fee = amount - (ulong)replacement.Outputs[0].Value.Satoshi;
        Assert.InRange(fee * 1000 / (ulong)(replacement.GetVirtualSize() * 4), 19_000UL, 20_500UL);
        harness.AssertAllInputsVerify(replacement);
        Assert.NotNull(bumps.GetOutput(row.TransactionId, row.OutputIndex, out var fresh));
        Assert.False(fresh);
        Assert.DoesNotContain(again, a => a is BroadcastAction);
    }

    [Fact]
    public async Task Given_AnOperatorBudget_When_TheRateWouldCostMore_Then_TheFeeStopsAtTheBudget()
    {
        // Arrange
        using var harness = new LocalCommitResolutionHarness();
        var bumps = new OperatorFeeBumps();
        var scheduler = CreateScheduler(harness, bumps);
        await harness.ResolveAsync();
        var toLocal = harness.VoutOf(OutputDescriptorKind.DelayedToLocal);
        await harness.MineToAsync(CloseHeight + Csv - 1);
        var original = Assert.Single(harness.Broadcast(BroadcastPurpose.Sweep));
        harness.HoldMempool = true;
        await harness.MineAsync();
        var row = harness.CommitmentRow(toLocal);
        var amount = OutputDescriptorData.TryDecode(row)!.AmountSat;
        var oldFee = amount - (ulong)original.Outputs[0].Value.Satoshi;
        var budget = oldFee * 3;

        // Act: a rate far above the budget
        bumps.SetOutput(row.TransactionId, row.OutputIndex,
                        new OperatorFeeBumpRequest(200_000, budget, null, null, false));
        var actions = await PlanAsync(harness, scheduler);

        // Assert
        var replacement = Transaction.Load(Assert.Single(actions.OfType<BroadcastAction>()).Transaction.RawTransaction,
                                           Network.Main);
        Assert.Equal(budget, amount - (ulong)replacement.Outputs[0].Value.Satoshi);
    }

    [Fact]
    public async Task Given_AResolvedOutput_When_Planning_Then_ItsRequestIsForgotten()
    {
        // Arrange: the sweep confirms
        using var harness = new LocalCommitResolutionHarness();
        var bumps = new OperatorFeeBumps();
        var scheduler = CreateScheduler(harness, bumps);
        await harness.ResolveAsync();
        var toLocal = harness.VoutOf(OutputDescriptorKind.DelayedToLocal);
        await harness.MineToAsync(CloseHeight + Csv - 1);
        var row = harness.CommitmentRow(toLocal);
        bumps.SetOutput(row.TransactionId, row.OutputIndex, new OperatorFeeBumpRequest(20_000, null, null, null, false));
        await harness.MineAsync();
        Assert.Equal(OutputResolutionState.Resolved, harness.CommitmentRow(toLocal).State);

        // Act
        await PlanAsync(harness, scheduler);

        // Assert
        Assert.Null(bumps.GetOutput(row.TransactionId, row.OutputIndex, out _));
    }

    [Fact]
    public void Given_ABudget_When_DecidingAReplacement_Then_TheBip125MinimumMustFitUnderIt()
    {
        // Arrange
        var policy = new SweepFeePolicy();

        // Act
        var tooSmall = policy.DecideReplacementWithBudget(100_000, 1_000, 500, 10_000, 1_100, 294);
        var capped = policy.DecideReplacementWithBudget(100_000, 1_000, 500, 100_000, 5_000, 294);

        // Assert
        Assert.Null(tooSmall);
        Assert.NotNull(capped);
        Assert.Equal(5_000UL, capped.FeeSat);
        Assert.True(capped.Capped);
    }

    private static SweepScheduler CreateScheduler(LocalCommitResolutionHarness harness, OperatorFeeBumps bumps) =>
        new(harness.GetService<IFeeService>(), harness.GetService<ILightningSigner>(),
            NullLogger<SweepScheduler>.Instance, new SweepFeePolicy(new SweepFeePolicyOptions { SweepConfTarget = 6 }),
            null, bumps);

    private static Task<IReadOnlyList<OutputResolverAction>> PlanAsync(LocalCommitResolutionHarness harness,
                                                                      SweepScheduler scheduler) =>
        scheduler.PlanAsync(harness.Close, harness.Rows.Values.ToList(), harness.Height,
                            harness.CreateUnitOfWorkForTests(), TestContext.Current.CancellationToken);
}