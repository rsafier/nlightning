using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Channels.Interfaces;
using Application.Channels.Services;
using Application.Channels.Switch;
using Application.Payments.FinalHop;
using Application.Payments.Onion;
using Application.Payments.Policy;
using Application.Payments.Switch;
using Channels.Handlers;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Domain.Serialization.Interfaces;
using static Channels.Handlers.NormalOperationTestContext;

/// <summary>
/// <see cref="HtlcSwitch"/> rules that the three-node proof does not reach: pruning order (NL-243), refusals, missing
/// origins and the registration.
/// </summary>
public class HtlcSwitchTests
{
    private static readonly ChannelId s_otherChannelId = new(Enumerable.Repeat((byte)0x99, 32).ToArray());

    private readonly NormalOperationTestContext _context = new();
    private readonly Mock<IChannelOperations> _operations = new();
    private readonly Mock<IForwardCircuitDbRepository> _circuits = new();
    private readonly Mock<ILocalPaymentHtlcHandler> _paymentHandler = new();
    private readonly List<string> _calls = [];

    public HtlcSwitchTests()
    {
        _context.UnitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(_circuits.Object);
        _context.ChannelStateDbRepository
                .Setup(r => r.PruneSettledHtlcsAsync(It.IsAny<ChannelId>(), It.IsAny<IEnumerable<HtlcKey>>()))
                .Callback(() => _calls.Add("prune"))
                .Returns(Task.CompletedTask);
        _circuits.Setup(r => r.UpdateAsync(It.IsAny<ForwardCircuitModel>()))
                 .Callback((ForwardCircuitModel c) => _calls.Add($"circuit {c.Status}"))
                 .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Given_SettledForwardWhoseUpstreamIsStillLockedIn_When_Settled_Then_TheRowIsKept()
    {
        // Arrange - the upstream fulfill was refused (peer away): the row carries the preimage for the next replay
        var incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(9));
        SetOrigin(4, HtlcOrigin.Forwarded(TestChannelId, incoming.Id));

        // Act
        await CreateSwitch().HandleAsync(Settled(4), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("prune", _calls);
    }

    [Fact]
    public async Task Given_SettledForwardWhoseUpstreamChannelIsNotLoaded_When_Settled_Then_TheRowIsKept()
    {
        // Arrange - at startup the outgoing channel can be registered before the incoming one
        SetOrigin(4, HtlcOrigin.Forwarded(s_otherChannelId, 2));

        // Act
        await CreateSwitch().HandleAsync(Settled(4), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("prune", _calls);
    }

    [Fact]
    public async Task Given_SettledForwardWithResolvedUpstreamAndCircuit_When_Settled_Then_TheRowIsPruned()
    {
        // Arrange - the upstream HTLC is gone from the incoming channel and the circuit is resolved
        SetOrigin(4, HtlcOrigin.Forwarded(TestChannelId, 7));
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, 7UL)).ReturnsAsync(Circuit(ForwardCircuitStatus.Failed));

        // Act
        await CreateSwitch().HandleAsync(Settled(4), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["prune"], _calls);
    }

    [Fact]
    public async Task Given_SettledForwardWhoseCircuitIsStillOffered_When_Settled_Then_TheRowIsKept()
    {
        // Arrange
        SetOrigin(4, HtlcOrigin.Forwarded(TestChannelId, 7));
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, 7UL))
                 .ReturnsAsync(Circuit(ForwardCircuitStatus.Offered));

        // Act
        await CreateSwitch().HandleAsync(Settled(4), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("prune", _calls);
    }

    [Fact]
    public async Task Given_SettledHtlcWithoutOrigin_When_Settled_Then_TheRowIsPruned()
    {
        // Act
        await CreateSwitch().HandleAsync(Settled(4), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["prune"], _calls);
    }

    [Fact]
    public async Task Given_PaymentHandlerFails_When_OurPaymentFailsAndSettles_Then_TheRowIsKeptUntilAHandledReplay()
    {
        // Arrange
        var hash = HashOf(SecretOf(3));
        SetOrigin(4, HtlcOrigin.Local(hash));
        var failed = new OutgoingHtlcFailed(s_otherChannelId, 4, hash, HtlcRemoval.Fail(new byte[] { 1 }));
        _paymentHandler.SetupSequence(h => h.HandleFailedAsync(failed, hash, It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException("database down"))
                       .Returns(Task.CompletedTask);
        var htlcSwitch = CreateSwitch();

        // Act
        await htlcSwitch.HandleAsync(failed, TestContext.Current.CancellationToken);
        await htlcSwitch.HandleAsync(Settled(4), TestContext.Current.CancellationToken);
        var prunedFirst = _calls.Contains("prune");
        await htlcSwitch.HandleAsync(failed, TestContext.Current.CancellationToken);
        await htlcSwitch.HandleAsync(Settled(4), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(prunedFirst);
        Assert.Equal(["prune"], _calls);
    }

    [Fact]
    public async Task Given_ForwardFulfilledDownstream_When_UpstreamIsRefused_Then_CircuitIsStillMarkedFulfilled()
    {
        // Arrange
        var preimage = SecretOf(9);
        var incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, preimage);
        SetOrigin(4, HtlcOrigin.Forwarded(TestChannelId, incoming.Id));
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, incoming.Id))
                 .ReturnsAsync(Circuit(ForwardCircuitStatus.Offered, incoming.Id));
        _operations.Setup(o => o.FulfillHtlcAsync(TestChannelId, incoming.Id, preimage, It.IsAny<CancellationToken>()))
                   .Callback(() => _calls.Add("fulfill"))
                   .ThrowsAsync(new CommitmentRefusedException("B2-NO-02", "peer away"));

        // Act
        await CreateSwitch().HandleAsync(new OutgoingHtlcFulfilled(s_otherChannelId, 4, incoming.PaymentHash, preimage),
                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["fulfill", "circuit Fulfilled"], _calls);
    }

    [Fact]
    public async Task Given_LockInOfAnHtlcAlreadyBeingRemoved_When_Replayed_Then_NothingHappens()
    {
        // Arrange - no locked-in HTLC 42 on the channel
        var lockedIn = new IncomingHtlcLockedIn(TestChannelId,
                                                new HtlcRecord(HtlcDirection.Incoming, 42, 1_000, HashOf(SecretOf(1)),
                                                               600, HtlcState.RcvdAddAckRevocation));

        // Act
        await CreateSwitch().HandleAsync(lockedIn, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_operations.Invocations);
        _circuits.Verify(r => r.GetByIncomingAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>()), Times.Never);
    }

    [Fact]
    public void Given_ApplicationRegistrations_When_AddingTheSwitchServices_Then_HtlcSwitchReplacesTheLocalOnlySwitch()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddChannelOperationsServices();

        // Act
        services.AddHtlcSwitchServices();

        // Assert
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IHtlcSwitch));
        Assert.Equal(typeof(HtlcSwitch), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotEqual(typeof(LocalOnlyHtlcSwitch), descriptor.ImplementationType);
    }

    [Fact]
    public async Task Given_OneKey_When_TwoCallersAcquire_Then_TheSecondWaitsAndTheEntryIsDroppedAfterwards()
    {
        // Arrange
        var locks = new KeyedAsyncLock<int>();
        var first = await locks.AcquireAsync(1, TestContext.Current.CancellationToken);

        // Act
        var second = locks.AcquireAsync(1, TestContext.Current.CancellationToken);
        var other = await locks.AcquireAsync(2, TestContext.Current.CancellationToken);
        var waitedWhileHeld = !second.IsCompleted;
        first.Dispose();
        (await second).Dispose();
        other.Dispose();

        // Assert
        Assert.True(waitedWhileHeld);
        Assert.Equal(0, locks.Count);
    }

    private HtlcSwitch CreateSwitch()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        var provider = services.BuildServiceProvider();
        var options = Options.Create(new NodeOptions { EnableHtlcs = true });
        var onionProcessor = new IncomingOnionProcessor(new Mock<ISphinxService>().Object,
                                                        new Mock<IHopPayloadSerializer>().Object,
                                                        new Mock<IOnionReplayCache>().Object,
                                                        NullLogger<IncomingOnionProcessor>.Instance);
        return new HtlcSwitch(new ChannelLockProvider(), _context.ChannelMemoryRepository.Object, _operations.Object,
                              new Mock<IFailureOnionService>().Object,
                              new FinalHopProcessor(NullLogger<FinalHopProcessor>.Instance),
                              new HtlcForwardingPolicy(options), NullLogger<HtlcSwitch>.Instance, onionProcessor,
                              new Mock<IPeerLivenessProbe>().Object, provider.GetRequiredService<IServiceScopeFactory>(),
                              localPaymentHandlers: [_paymentHandler.Object]);
    }

    private void SetOrigin(ulong outgoingHtlcId, HtlcOrigin origin) =>
        _context.ChannelStateDbRepository
                .Setup(r => r.GetHtlcOriginAsync(s_otherChannelId, new HtlcKey(HtlcDirection.Outgoing, outgoingHtlcId)))
                .ReturnsAsync(origin);

    private static OutgoingHtlcSettled Settled(ulong htlcId) =>
        new(s_otherChannelId, htlcId, HashOf(SecretOf(1)), HtlcRemovalKind.Fail);

    private static ForwardCircuitModel Circuit(ForwardCircuitStatus status, ulong incomingHtlcId = 7)
    {
        var now = DateTimeOffset.UtcNow;
        return ForwardCircuitModel.Restore(TestChannelId, incomingHtlcId, LightningMoney.MilliSatoshis(30_000_000),
                                           640, HashOf(SecretOf(9)), SecretOf(0x5E), new ShortChannelId(1, 2, 3),
                                           LightningMoney.MilliSatoshis(29_000_000), 600, now, status,
                                           status == ForwardCircuitStatus.Pending ? null : s_otherChannelId,
                                           status == ForwardCircuitStatus.Pending ? null : 4UL,
                                           status is ForwardCircuitStatus.Fulfilled or ForwardCircuitStatus.Failed
                                               ? now
                                               : null);
    }
}