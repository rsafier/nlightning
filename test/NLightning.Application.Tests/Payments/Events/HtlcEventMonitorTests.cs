using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Payments.Events;

using Application.Payments.Events;
using Channels.Handlers;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Payments.Events;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Enums;
using Infrastructure.Crypto.Hashes;
using static Channels.Handlers.NormalOperationTestContext;

public class HtlcEventMonitorTests
{
    private static HtlcEventMonitor Monitor(NormalOperationTestContext context, HtlcEventHub hub)
    {
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IUnitOfWork))).Returns(context.UnitOfWork.Object);
        var scope = new Mock<IServiceScope>();
        scope.SetupGet(s => s.ServiceProvider).Returns(provider.Object);
        var scopes = new Mock<IServiceScopeFactory>();
        scopes.Setup(s => s.CreateScope()).Returns(scope.Object);
        return new HtlcEventMonitor(hub, context.ChannelMemoryRepository.Object, scopes.Object,
                                   NullLogger<HtlcEventMonitor>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_CommittedOutgoingAdd_When_Monitoring_Then_SendCarriesTheExactOfferedLeg(bool failSave)
    {
        var context = new NormalOperationTestContext();
        var hub = new HtlcEventHub();
        using var subscription = hub.Subscribe();
        var result = context.State.SendAdd(123_000, HashOf(SecretOf(1)), 700, Onion, null);
        context.ChannelStateDbRepository.Setup(r => r.GetHtlcOriginAsync(TestChannelId,
            new HtlcKey(HtlcDirection.Outgoing, 0))).ReturnsAsync(HtlcOrigin.Local(HashOf(SecretOf(1))));
        if (failSave)
            context.FailSaves();
        using var monitor = Monitor(context, hub);
        var transitions = context.CreateTransitions(monitor);
        if (failSave)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => transitions.CommitAsync(context.Channel, result));
            subscription.Dispose();
            await using var empty = subscription.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.False(await empty.MoveNextAsync());
            return;
        }
        await transitions.CommitAsync(context.Channel, result);
        await monitor.WhenIdleAsync(TestContext.Current.CancellationToken);
        var activity = await ReadOne(subscription);
        Assert.Equal(HtlcActivityKind.Forward, activity.Kind);
        Assert.Equal(HtlcActivityRole.Send, activity.Role);
        Assert.Equal(123_000UL, activity.OutgoingAmountMsat);
        Assert.Equal(700U, activity.OutgoingTimelock);
        Assert.Equal(0UL, activity.IncomingChannelId);
        Assert.Empty(context.Events.Drain());
    }

    [Fact]
    public async Task Given_LocalRefusal_When_RemovalCommits_Then_ClearWireFailureSurvivesEncryption()
    {
        var context = new NormalOperationTestContext();
        var record = context.LockIn(HtlcDirection.Incoming, 123_000, SecretOf(2));
        var circuits = new Mock<IForwardCircuitDbRepository>();
        context.UnitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(circuits.Object);
        var hub = new HtlcEventHub();
        using var subscription = hub.Subscribe();
        using var monitor = Monitor(context, hub);
        monitor.ClassifyIncoming(TestChannelId, record.Id, HtlcActivityRole.Receive);
        using (monitor.WithLocalFailure((ushort)FailureCode.IncorrectOrUnknownPaymentDetails))
            await context.CreateTransitions(monitor).CommitAsync(context.Channel,
                context.State.SendFail(record.Id, new byte[292]));
        await monitor.WhenIdleAsync(TestContext.Current.CancellationToken);
        var activity = await ReadOne(subscription);
        Assert.Equal(HtlcActivityKind.LinkFail, activity.Kind);
        Assert.Equal(HtlcActivityRole.Receive, activity.Role);
        Assert.Equal((ushort)FailureCode.IncorrectOrUnknownPaymentDetails, activity.WireFailure);
        Assert.Equal(123_000UL, activity.IncomingAmountMsat);
    }

    [Fact]
    public async Task Given_PairedForward_When_AddCommits_Then_BothCircuitIdsAmountsAndTimelocksArePreserved()
    {
        var context = new NormalOperationTestContext();
        context.Channel.ShortChannelId = new ShortChannelId(123UL);
        var circuits = new Mock<IForwardCircuitDbRepository>();
        var circuit = new ForwardCircuitModel(TestChannelId, 42, LightningMoney.MilliSatoshis(125_000), 720,
            HashOf(SecretOf(1)), SecretOf(3), new ShortChannelId(123UL),
            LightningMoney.MilliSatoshis(123_000), 700, DateTimeOffset.UtcNow);
        circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, 42)).ReturnsAsync(circuit);
        context.UnitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(circuits.Object);
        context.ChannelStateDbRepository.Setup(r => r.GetHtlcOriginAsync(TestChannelId,
            new HtlcKey(HtlcDirection.Outgoing, 0))).ReturnsAsync(HtlcOrigin.Forwarded(TestChannelId, 42));
        var hub = new HtlcEventHub();
        using var subscription = hub.Subscribe();
        using var monitor = Monitor(context, hub);
        await context.CreateTransitions(monitor).CommitAsync(context.Channel,
            context.State.SendAdd(123_000, HashOf(SecretOf(1)), 700, Onion));
        await monitor.WhenIdleAsync(TestContext.Current.CancellationToken);
        var activity = await ReadOne(subscription);
        Assert.Equal(HtlcActivityRole.Forward, activity.Role);
        Assert.Equal(42UL, activity.IncomingHtlcId);
        Assert.Equal(0UL, activity.OutgoingHtlcId);
        Assert.Equal(123UL, activity.IncomingChannelId);
        Assert.Equal(123UL, activity.OutgoingChannelId);
        Assert.Equal(125_000UL, activity.IncomingAmountMsat);
        Assert.Equal(123_000UL, activity.OutgoingAmountMsat);
        Assert.Equal(720U, activity.IncomingTimelock);
        Assert.Equal(700U, activity.OutgoingTimelock);
    }

    [Fact]
    public async Task Given_KnownPreimageAcrossReconnect_When_FulfillIsResent_Then_NoDuplicatePassiveSettle()
    {
        var context = new NormalOperationTestContext();
        var preimage = SecretOf(2);
        var record = context.LockIn(HtlcDirection.Outgoing, 123_000, preimage);
        context.ChannelStateDbRepository.Setup(r => r.GetHtlcOriginAsync(TestChannelId, record.Key))
            .ReturnsAsync(HtlcOrigin.Local(HashOf(preimage)));
        var hub = new HtlcEventHub();
        using var subscription = hub.Subscribe();
        using var monitor = Monitor(context, hub);
        var transitions = context.CreateTransitions(monitor);
        using var sha = new Sha256();
        await transitions.CommitAsync(context.Channel, context.State.ReceiveFulfill(record.Id, preimage, sha));
        await monitor.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HtlcActivityKind.Settle, (await ReadOne(subscription)).Kind);
        context.SetState(context.State.RevertUncommitted().Next);
        await transitions.CommitAsync(context.Channel, context.State.ReceiveFulfill(record.Id, preimage, sha));
        await monitor.WhenIdleAsync(TestContext.Current.CancellationToken);
        subscription.Dispose();
        await using var reader = subscription.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Given_MonitoringLookupFailsAfterSave_When_Committing_Then_NodeContinuesAndReadersAreInvalidated()
    {
        var context = new NormalOperationTestContext();
        context.ChannelStateDbRepository.Setup(r => r.GetHtlcOriginAsync(It.IsAny<ChannelId>(), It.IsAny<HtlcKey>()))
            .ThrowsAsync(new InvalidOperationException("monitoring read unavailable"));
        var hub = new HtlcEventHub();
        using var subscription = hub.Subscribe();
        using var monitor = Monitor(context, hub);
        await context.CreateTransitions(monitor).CommitAsync(context.Channel,
            context.State.SendAdd(123_000, HashOf(SecretOf(1)), 700, Onion));
        Assert.Equal(1UL, context.State.LocalNextHtlcId);
        await monitor.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.True(subscription.Overflowed);
        Assert.True(subscription.OverflowCancellationToken.IsCancellationRequested);
        Assert.False(hub.HasSubscribers);
    }

    [Fact]
    public async Task Given_StalledObserver_When_FastPaymentOriginIsPruned_Then_FundsPathContinuesAndCapturedOriginSurvives()
    {
        var context = new NormalOperationTestContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<Domain.Payments.ValueObjects.HtlcOrigin?>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.ChannelStateDbRepository.Setup(r => r.GetHtlcOriginAsync(TestChannelId,
            new HtlcKey(HtlcDirection.Outgoing, 0))).Returns(() =>
            {
                entered.TrySetResult();
                return release.Task;
            });
        context.ChannelStateDbRepository.Setup(r => r.GetHtlcOriginAsync(TestChannelId,
            new HtlcKey(HtlcDirection.Outgoing, 1))).ThrowsAsync(new InvalidOperationException("already pruned"));
        var hub = new HtlcEventHub();
        using var subscription = hub.Subscribe();
        using var monitor = Monitor(context, hub);
        var transitions = context.CreateTransitions(monitor);
        await transitions.CommitAsync(context.Channel, context.State.SendAdd(120_000, HashOf(SecretOf(1)), 700, Onion));
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var preimage = SecretOf(2);
        using (monitor.WithOrigin(HtlcOrigin.Local(HashOf(preimage))))
            await transitions.CommitAsync(context.Channel, context.State.SendAdd(123_000, HashOf(preimage), 700, Onion));
        var locked = context.State.SendCommit(context.Ports).Next;
        locked = locked.ReceiveRevoke(SecretOf(0x90), Point(0x22), context.Ports).Next;
        locked = locked.ReceiveCommit(context.Ports.SignaturesFor(locked), context.Ports).Next;
        context.SetState(locked);
        using var sha = new Sha256();
        await transitions.CommitAsync(context.Channel, context.State.ReceiveFulfill(1, preimage, sha));
        Assert.False(release.Task.IsCompleted); // Committed offers/outcome did not wait for the stalled observer read.
        release.SetResult(HtlcOrigin.Local(HashOf(SecretOf(1))));
        await monitor.WhenIdleAsync(TestContext.Current.CancellationToken);
        Assert.False(subscription.Overflowed);
        Assert.Equal(0UL, (await ReadOne(subscription)).OutgoingHtlcId);
        Assert.Equal(1UL, (await ReadOne(subscription)).OutgoingHtlcId);
        var settle = await ReadOne(subscription);
        Assert.Equal(HtlcActivityKind.Settle, settle.Kind);
        Assert.Equal(HtlcActivityRole.Send, settle.Role);
        Assert.Equal(1UL, settle.OutgoingHtlcId);
        context.ChannelStateDbRepository.Verify(r => r.GetHtlcOriginAsync(TestChannelId,
            new HtlcKey(HtlcDirection.Outgoing, 1)), Times.Never);
    }

    private static async Task<HtlcActivityEvent> ReadOne(IHtlcEventSubscription subscription)
    {
        await using var reader = subscription.ReadAllAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());
        return reader.Current;
    }
}