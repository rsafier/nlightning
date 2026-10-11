using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Accounting;
using Application.Onchain.Fees;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public sealed partial class OnchainResolutionExecutorTests
{
    private OnchainResolutionExecutor CreateObservedExecutor(List<HtlcActivityEvent> activities,
                                                              out ServiceProvider provider, bool throwPublication = false)
    {
        var publisher = new Mock<IHtlcEventPublisher>();
        Action<HtlcActivityEvent> publish = activities.Add;
        if (throwPublication)
        {
            publish = _ => throw new InvalidOperationException("test publisher failure");
            publisher.Setup(p => p.InvalidateSubscriptions()).Throws(new InvalidOperationException("test invalidation failure"));
        }
        publisher.Setup(p => p.CapturePublisher()).Returns(publish);
        var services = new ServiceCollection();
        services.AddScoped(_ => _store.CreateUnitOfWork().Object);
        services.AddScoped<IOutputResolver>(_ => _resolver);
        services.AddSingleton<ISweepScheduler>(_sweepScheduler);
        services.AddSingleton(_htlcSwitch.Object);
        services.AddSingleton<IBitcoinChainService>(_readingChain);
        services.AddSingleton(publisher.Object);
        provider = services.BuildServiceProvider();
        return new OnchainResolutionExecutor(_broadcaster.Object, new ChannelLockProvider(), _memory.Object,
            NullLogger<OnchainResolutionExecutor>.Instance, _outpointWatcher.Object,
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_OnchainOutcome_When_ReplayedAndExecutorRestarts_Then_OnePassiveEventButEverySwitchReplay(bool settled)
    {
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var outcome = OutgoingOutcome(settled);
        _resolver.OnResolve = (_, _, _) => [new RaiseChannelEventAction(outcome)];
        var activities = new List<HtlcActivityEvent>();
        var executor = CreateObservedExecutor(activities, out var provider);
        using (provider)
        {
            await executor.RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);
            await executor.RunRoundAsync(SpentAt + 6, TestContext.Current.CancellationToken);
        }
        var restarted = CreateObservedExecutor(activities, out var restartedProvider);
        using (restartedProvider)
            await restarted.RunRoundAsync(SpentAt + 7, TestContext.Current.CancellationToken);

        var activity = Assert.Single(activities);
        Assert.Equal(settled ? HtlcActivityKind.Settle : HtlcActivityKind.ForwardFail, activity.Kind);
        Assert.Equal(settled, activity.Settled);
        Assert.False(activity.Offchain);
        Assert.Single(_store.HtlcObservations);
        Assert.Single(_store.Saves); // Pure replay does not force an otherwise empty save.
        Assert.Equal(3, _calls.Count(call => call == "switch"));
        Assert.Equal("save", _calls[0]);
    }

    [Fact]
    public async Task Given_OutcomeSaveFails_When_Retried_Then_NoPrematurePublicationAndCheckpointRetries()
    {
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        _resolver.OnResolve = (_, _, _) => [new RaiseChannelEventAction(OutgoingOutcome(true))];
        var activities = new List<HtlcActivityEvent>();
        var executor = CreateObservedExecutor(activities, out var provider);
        using (provider)
        {
            _store.FailNextSave = new InvalidOperationException("test save failure");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executor.ResolveChannelAsync(_channel.ChannelId, SpentAt + 5, TestContext.Current.CancellationToken));
            Assert.Empty(activities);
            Assert.Empty(_store.HtlcObservations);
            Assert.DoesNotContain("switch", _calls);
            await executor.RunRoundAsync(SpentAt + 6, TestContext.Current.CancellationToken);
        }
        Assert.Single(activities);
        Assert.Single(_store.HtlcObservations);
    }

    [Fact]
    public async Task Given_FailedOnchainHtlc_When_LaterPreimageArrivesAfterReorg_Then_EachDistinctOutcomeAppearsOnce()
    {
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        _resolver.OnResolve = (_, _, _) => [new RaiseChannelEventAction(OutgoingOutcome(false))];
        var activities = new List<HtlcActivityEvent>();
        var executor = CreateObservedExecutor(activities, out var provider);
        using (provider)
        {
            await executor.RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);
            // A replacement close can expose a previously unknown preimage; the failure notification is never undone.
            _resolver.OnResolve = (_, _, _) => [new RaiseChannelEventAction(OutgoingOutcome(true))];
            await executor.RunRoundAsync(SpentAt + 6, TestContext.Current.CancellationToken);
            await executor.RunRoundAsync(SpentAt + 7, TestContext.Current.CancellationToken);
        }
        Assert.Equal([HtlcActivityKind.ForwardFail, HtlcActivityKind.Settle], activities.Select(a => a.Kind));
        Assert.Equal(2, _store.HtlcObservations.Count);
        Assert.Equal(3, _calls.Count(call => call == "switch"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_IncomingResolution_When_ReconfirmedAfterReorg_Then_OneOnchainFinal(bool settled)
    {
        AddOutput(0, OutputDescriptorKind.LocalReceivedHtlc);
        var key = (s_commitmentTxId, 0U);
        var spend = CreateSpend(s_commitmentTxId, 0);
        var watch = new WatchedOutpointModel(s_commitmentTxId, 0, _channel.ChannelId,
            WatchedOutpointPurpose.ResolutionOutput);
        watch.MarkSpent(spend.TxId, SpentAt + 1, Hash.Empty);
        _store.Watches[key] = watch;
        _store.Outputs[key] = _store.Outputs[key] with
        {

            HtlcDirection = HtlcDirection.Incoming,
            HtlcId = 77,
            State = OutputResolutionState.Resolved,
            ResolvedHeight = SpentAt + 1,
            ResolvingTransactionId = settled ? spend.TxId : (Domain.Bitcoin.ValueObjects.TxId?)null
        };
        var activities = new List<HtlcActivityEvent>();
        var executor = CreateObservedExecutor(activities, out var provider);
        using (provider)
        {
            await executor.RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);
            watch.ClearSpend();
            await executor.RunRoundAsync(SpentAt + 6, TestContext.Current.CancellationToken);
            Assert.Equal(settled ? OutputResolutionState.Broadcast : OutputResolutionState.Pending,
                         _store.Outputs[key].State);
            watch.MarkSpent(spend.TxId, SpentAt + 7, Hash.Empty);
            _store.Outputs[key] = _store.Outputs[key] with
            {

                State = OutputResolutionState.Resolved,
                ResolvedHeight = SpentAt + 7,
                ResolvingTransactionId = settled ? spend.TxId : (Domain.Bitcoin.ValueObjects.TxId?)null
            };
            await executor.RunRoundAsync(SpentAt + 8, TestContext.Current.CancellationToken);
        }
        var activity = Assert.Single(activities);
        Assert.Equal(HtlcActivityKind.Final, activity.Kind);
        Assert.Equal(77UL, activity.IncomingHtlcId);
        Assert.Equal(settled, activity.Settled);
        Assert.False(activity.Offchain);
    }

    [Fact]
    public async Task Given_TrimmedIncomingHtlc_When_CloseIsDeepAndExecutorRestarts_Then_OneFailedOnchainFinal()
    {
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        _store.AccountingEvents.Add((new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ChannelForceClosed(_channel.ChannelId, s_commitmentTxId),
            Kind = AccountingEventKind.ChannelForceClosed,
            OccurredAt = DateTimeOffset.UtcNow,
            BlockHeight = SpentAt,
            Details = new Dictionary<string, string>
            {
                [OnchainAccounting.TrimmedIncomingHtlcsKey] = $"88:{Hash.Empty}"
            }
        }, -1));
        var activities = new List<HtlcActivityEvent>();
        var executor = CreateObservedExecutor(activities, out var provider);
        using (provider)
        {
            await executor.RunRoundAsync(SpentAt + 4, TestContext.Current.CancellationToken);
            Assert.Empty(activities); // Five confirmations; default reasonable depth is six.
            await executor.RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);
            await executor.RunRoundAsync(SpentAt + 6, TestContext.Current.CancellationToken);
        }
        var restarted = CreateObservedExecutor(activities, out var restartedProvider);
        using (restartedProvider)
            await restarted.RunRoundAsync(SpentAt + 7, TestContext.Current.CancellationToken);
        var activity = Assert.Single(activities);
        Assert.Equal(HtlcActivityKind.Final, activity.Kind);
        Assert.Equal(88UL, activity.IncomingHtlcId);
        Assert.False(activity.Settled);
        Assert.False(activity.Offchain);
        Assert.Single(_store.HtlcObservations);
    }

    [Fact]
    public async Task Given_PassiveFanoutFails_When_OnchainOutcomeCommitted_Then_OperationalSwitchStillRuns()
    {
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        _resolver.OnResolve = (_, _, _) => [new RaiseChannelEventAction(OutgoingOutcome(true))];
        var executor = CreateObservedExecutor([], out var provider, throwPublication: true);
        using (provider)
            await executor.RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);
        Assert.Single(_store.HtlcObservations);
        Assert.Contains("switch", _calls);
    }

    private IChannelDomainEvent OutgoingOutcome(bool settled) => settled
        ? new OutgoingHtlcFulfilled(_channel.ChannelId, 4, Hash.Empty, new Secret(new byte[32]))
        : new OutgoingHtlcFailed(_channel.ChannelId, 4, Hash.Empty, HtlcRemoval.OnchainTimeout());
}