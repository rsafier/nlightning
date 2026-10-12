namespace NLightning.Application.Tests.Onchain;

using Domain.Onchain.Enums;
using Domain.Onchain.Models;

public sealed partial class OnchainResolutionExecutorTests
{
    [Fact]
    public async Task Given_InitialSigningWorkflow_When_ExecutorAppliesItsRound_Then_ReceiptSavePrecedesPublicationAndRbfWaits()
    {
        // Arrange: the resolver owns the signing lifecycle and stages its consumption with the sweep decision.
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var sweep = CreateBroadcast(0x61);
        var consumed = false;
        _resolver.OnResolve = (_, outputs, _) =>
        [
            new SigningWorkflowRoundAction(Guid.NewGuid()),
            new UpsertOutputAction(outputs[0] with
            { State = OutputResolutionState.Broadcast, ResolvingTransactionId = sweep.TransactionId }),
            new BroadcastAction(sweep),
            new StageWriteAction("consume receipt", (_, _) =>
            { consumed = true; _calls.Add("consume receipt"); return Task.CompletedTask; })
        ];
        _sweepScheduler.OnPlan = (_, _, _) => throw new InvalidOperationException("RBF must wait for signing ownership to commit.");

        // Act
        await CreateExecutor().RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);

        // Assert: the executor consumes and saves before publishing, without starting a competing signing workflow.
        Assert.True(consumed);
        Assert.Null(_sweepScheduler.LastHeight);
        Assert.Single(_store.Saves);
        Assert.Equal(["consume receipt", "save", "publish Sweep"], _calls);
    }

    [Fact]
    public async Task Given_ObsoleteInitialIntent_When_ExecutorRetiresIt_Then_StageOnlyRetirementIsSavedAndNextRoundCanBump()
    {
        // Arrange: retirement has no transaction or output upsert but still requires an executor save.
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        _resolver.OnResolve = (_, _, _) =>
        [
            new SigningWorkflowRoundAction(Guid.NewGuid()),
            new StageWriteAction("retire intent", (_, _) =>
            { _calls.Add("retire intent"); return Task.CompletedTask; })
        ];
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);

        // Assert: a real save commits terminal ownership; fee bumping resumes in a later unowned round.
        Assert.Equal(["retire intent", "save"], _calls);
        Assert.Single(_store.Saves);
        Assert.Null(_sweepScheduler.LastHeight);
        _resolver.OnResolve = (_, _, _) => [];
        await executor.RunRoundAsync(SpentAt + 6, TestContext.Current.CancellationToken);
        Assert.Equal(SpentAt + 6, _sweepScheduler.LastHeight);
    }
}