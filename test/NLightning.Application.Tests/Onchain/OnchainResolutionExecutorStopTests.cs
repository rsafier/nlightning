namespace NLightning.Application.Tests.Onchain;

using Domain.Bitcoin.Events;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>
/// NL-1355: <see cref="Application.Onchain.OnchainResolutionExecutor.StopAsync"/> refuses new rounds, cancels the one
/// running and waits for it, and nothing the executor does after the stop was requested is saved, published or raised
/// to the switch.
/// </summary>
public sealed partial class OnchainResolutionExecutorTests
{
    [Fact]
    public async Task Given_ScheduledRoundRunning_When_Stopped_Then_StopWaitsForItAndNothingIsSavedOrPublished()
    {
        // Arrange: the block's round is held in the resolver, which then asks for a sweep
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var sweep = CreateBroadcast(0x51);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _resolver.BeforeResolve = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        _resolver.OnResolve = (_, outputs, _) =>
        [
            new UpsertOutputAction(outputs[0] with
            {
                State = OutputResolutionState.Broadcast, ResolvingTransactionId = sweep.TransactionId
            }),
            new BroadcastAction(sweep),
            new RaiseChannelEventAction(new OutgoingHtlcFulfilled(_channel.ChannelId, 4, new Hash(new byte[32]),
                                                                  new Secret(new byte[32])))
        ];
        var executor = CreateExecutor();
        executor.ScheduleRound(SpentAt + 5);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Act
        var stop = executor.StopAsync(TestContext.Current.CancellationToken);
        var waitedForRound = !stop.IsCompleted;
        release.SetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert: the stop waited for the round, which saved, published and raised nothing
        Assert.True(waitedForRound);
        Assert.Empty(_store.Saves);
        Assert.Empty(_calls);
        _broadcaster.Verify(b => b.PublishAsync(It.IsAny<BroadcastTransactionModel>()), Times.Never);
        _htlcSwitch.Verify(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()),
                           Times.Never);
    }

    [Fact]
    public async Task Given_StopRequestedAfterTheSave_When_RoundGoesOn_Then_NothingIsPublishedOrRaised()
    {
        // Arrange: the stop is requested while the round's save runs
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var sweep = CreateBroadcast(0x52);
        _resolver.OnResolve = (_, outputs, _) =>
        [
            new UpsertOutputAction(outputs[0] with
            {
                State = OutputResolutionState.Broadcast, ResolvingTransactionId = sweep.TransactionId
            }),
            new BroadcastAction(sweep),
            new WatchOutpointAction(new WatchedOutpointModel(sweep.TransactionId, 0, _channel.ChannelId,
                                                             WatchedOutpointPurpose.ResolutionOutput)),
            new RaiseChannelEventAction(new OutgoingHtlcFulfilled(_channel.ChannelId, 4, new Hash(new byte[32]),
                                                                  new Secret(new byte[32])))
        ];
        var executor = CreateExecutor();
        Task? stop = null;
        _store.OnSave = () =>
        {
            _calls.Add("save");
            stop = executor.StopAsync(TestContext.Current.CancellationToken);
        };

        // Act
        await executor.RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);
        await stop!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert: the saved broadcast row waits for the chain monitor after the next start
        Assert.Single(_store.Saves);
        Assert.Equal(["save"], _calls);
        _broadcaster.Verify(b => b.PublishAsync(It.IsAny<BroadcastTransactionModel>()), Times.Never);
        _htlcSwitch.Verify(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()),
                           Times.Never);
    }

    [Fact]
    public async Task Given_Stopped_When_BlockArrivesOrSpendIsHandled_Then_NothingRuns()
    {
        // Arrange
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var executor = CreateExecutor();
        await executor.StopAsync(TestContext.Current.CancellationToken);

        // Act: a block, a direct round, a channel round and a spend after the stop; a second stop
        executor.ScheduleRound(SpentAt + 5);
        await executor.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(SpentAt + 6, TestContext.Current.CancellationToken);
        await executor.ResolveChannelAsync(_channel.ChannelId, SpentAt + 6, TestContext.Current.CancellationToken);
        await executor.HandleOutputSpentAsync(
            new OutpointSpentEventArgs(_channel.ChannelId, CreateSpend(s_commitmentTxId, 0), SpentAt + 6, 1,
                                       s_commitmentTxId, 0, OnchainTestStore.BlockHash(6)),
            TestContext.Current.CancellationToken);
        await executor.StopAsync(TestContext.Current.CancellationToken);

        // Assert: the resolver was never asked and nothing was saved
        Assert.Null(_resolver.LastHeight);
        Assert.Empty(_resolver.Spends);
        Assert.Empty(_store.Saves);
        Assert.Empty(_calls);
    }
}