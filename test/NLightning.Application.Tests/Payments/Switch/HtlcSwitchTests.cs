using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Channels.Fees;
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
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
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

        // Assert: IHtlcSwitch is the container's own HtlcSwitch singleton
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IHtlcSwitch));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        Assert.NotNull(descriptor.ImplementationFactory);
        Assert.NotEqual(typeof(LocalOnlyHtlcSwitch), descriptor.ImplementationType);
        var own = Assert.Single(services, d => d.ServiceType == typeof(HtlcSwitch));
        Assert.Equal(typeof(HtlcSwitch), own.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, own.Lifetime);
    }

    [Fact]
    public async Task Given_SwitchDecoratedByTheDustExposureSwitch_When_TheProviderIsDisposed_Then_TheSwitchIsDisposed()
    {
        // Arrange: the production order, AddHtlcSwitchServices then AddChannelFeeServices (whose decorator is not
        // IDisposable): the mpp_timeout timers must still stop with the host
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IPeerLivenessProbe>().Object);
        services.AddChannelOperationsServices();
        services.AddSingleton<IChannelLockProvider>(new ChannelLockProvider());
        services.AddSingleton(_context.ChannelMemoryRepository.Object);
        services.AddSingleton(_operations.Object);
        services.AddSingleton(new Mock<IFailureOnionService>().Object);
        services.AddSingleton(new Mock<Domain.Bitcoin.Interfaces.IFeeService>().Object);
        services.AddSingleton(new FinalHopProcessor(NullLogger<FinalHopProcessor>.Instance));
        services.AddSingleton<IForwardingPolicy>(
            new HtlcForwardingPolicy(Options.Create(new NodeOptions { EnableHtlcs = true })));
        services.AddSingleton(Options.Create(new NodeOptions { EnableHtlcs = true }));
        services.AddSingleton(new IncomingOnionProcessor(new Mock<ISphinxService>().Object,
                                                         new Mock<IHopPayloadSerializer>().Object,
                                                         new Mock<IOnionReplayStore>().Object,
                                                         NullLogger<IncomingOnionProcessor>.Instance));
        services.AddHtlcSwitchServices();
        services.AddChannelFeeServices();
        var provider = services.BuildServiceProvider();
        var decorated = Assert.IsType<DustExposureHtlcSwitch>(provider.GetRequiredService<IHtlcSwitch>());
        var htlcSwitch = Assert.IsType<HtlcSwitch>(decorated.Inner);
        Assert.Same(provider.GetRequiredService<HtlcSwitch>(), htlcSwitch);

        // Act
        await provider.DisposeAsync();

        // Assert
        Assert.True(htlcSwitch.IsDisposed);
    }

    [Fact]
    public void Given_ApplicationRegistrations_When_AddingTheSwitchServicesTwice_Then_TheProbeIsDecoratedOnce()
    {
        // Arrange - a stand-in for ConnectedPeerLivenessProbe (which needs the peer manager)
        var inner = new Mock<IPeerLivenessProbe>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(inner.Object);
        services.AddChannelOperationsServices();
        services.AddSingleton(new Mock<IChannelLockProvider>().Object);
        services.AddSingleton(_context.ChannelMemoryRepository.Object);

        // Act
        services.AddHtlcSwitchServices();
        services.AddHtlcSwitchServices();

        // Assert - one decorator around the probe registered first, one replayer
        Assert.Single(services, d => d.ServiceType == typeof(LinkUpEventReplayer));
        using var provider = services.BuildServiceProvider();
        var probe = Assert.IsType<LinkUpReplayingPeerLivenessProbe>(provider.GetRequiredService<IPeerLivenessProbe>());
        Assert.Same(inner.Object, probe.Inner);
    }

    [Fact]
    public void Given_NoLivenessProbe_When_AddingTheSwitchServices_Then_Throws()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() => services.AddHtlcSwitchServices());
    }

    [Fact]
    public async Task Given_IncomingHtlcSettled_When_Handled_Then_ItsArchivedRowIsPrunedInOneSave()
    {
        // Arrange - NL-243: nothing reads a final incoming row
        var settled = new IncomingHtlcSettled(TestChannelId, 3, HashOf(SecretOf(1)), HtlcRemovalKind.Fulfill);

        // Act
        await CreateSwitch().HandleAsync(settled, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["prune"], _calls);
        _context.ChannelStateDbRepository.Verify(
            r => r.PruneSettledHtlcsAsync(TestChannelId,
                                          It.Is<IEnumerable<HtlcKey>>(k => k.Single() == new HtlcKey(
                                                                                HtlcDirection.Incoming, 3))),
            Times.Once);
        _context.UnitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
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

    [Fact]
    public async Task Given_AttributionAdvertised_When_ForwardIsFulfilledDownstream_Then_UpstreamGetsTheWrappedAttribution()
    {
        // Arrange - the switch seam (NL-326): wrap the downstream attribution_data and payload with our hold time
        var preimage = SecretOf(9);
        var incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, preimage);
        SetOrigin(4, HtlcOrigin.Forwarded(TestChannelId, incoming.Id));
        var circuit = Circuit(ForwardCircuitStatus.Offered, incoming.Id);
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, incoming.Id)).ReturnsAsync(circuit);
        _operations.Setup(o => o.GetHoldTimeAsync(TestChannelId, incoming.Id, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(7u);
        var downstream = Enumerable.Repeat((byte)0xAD, 920).ToArray();
        var payload = new byte[] { 1, 2, 3 };
        var wrapped = new AttributedFulfillment(new byte[920], null);
        var attribution = new RecordingAttributionService { Fulfillment = wrapped };
        _operations.Setup(o => o.FulfillHtlcAsync(TestChannelId, incoming.Id, preimage, wrapped, null,
                                                  It.IsAny<CancellationToken>()))
                   .Callback(() => _calls.Add("attributed fulfill"))
                   .Returns(Task.CompletedTask);

        // Act
        await CreateSwitch(attribution, advertiseAttribution: true)
           .HandleAsync(new OutgoingHtlcFulfilled(s_otherChannelId, 4, incoming.PaymentHash, preimage, downstream,
                                                  payload), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["attributed fulfill", "circuit Fulfilled"], _calls);
        var call = Assert.Single(attribution.Calls);
        Assert.Equal("wrap fulfillment", call.Call);
        Assert.Equal(circuit.IncomingSharedSecret, call.Secret);
        Assert.Equal(downstream, call.First);
        Assert.Equal(payload, call.Second);
        Assert.Equal(7u, call.HoldTime);
        _operations.Verify(o => o.FulfillHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(), It.IsAny<Secret>(),
                                                   It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_AttributionNotAdvertised_When_ForwardIsFulfilledDownstream_Then_UpstreamGetsNoAttribution()
    {
        // Arrange - BOLT 4: attribution_data only when we advertise option_attribution_data
        var preimage = SecretOf(9);
        var incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, preimage);
        SetOrigin(4, HtlcOrigin.Forwarded(TestChannelId, incoming.Id));
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, incoming.Id))
                 .ReturnsAsync(Circuit(ForwardCircuitStatus.Offered, incoming.Id));
        _operations.Setup(o => o.FulfillHtlcAsync(TestChannelId, incoming.Id, preimage, It.IsAny<CancellationToken>()))
                   .Callback(() => _calls.Add("fulfill"))
                   .Returns(Task.CompletedTask);
        var attribution = new RecordingAttributionService();

        // Act
        await CreateSwitch(attribution)
           .HandleAsync(new OutgoingHtlcFulfilled(s_otherChannelId, 4, incoming.PaymentHash, preimage,
                                                  new byte[920]), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["fulfill", "circuit Fulfilled"], _calls);
        Assert.Empty(attribution.Calls);
    }

    [Fact]
    public async Task Given_AttributionAdvertised_When_ForwardFailsDownstream_Then_UpstreamGetsTheWrappedFailure()
    {
        // Arrange
        var preimage = SecretOf(9);
        var incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, preimage);
        SetOrigin(4, HtlcOrigin.Forwarded(TestChannelId, incoming.Id));
        var circuit = Circuit(ForwardCircuitStatus.Offered, incoming.Id);
        _circuits.Setup(r => r.GetByIncomingAsync(TestChannelId, incoming.Id)).ReturnsAsync(circuit);
        _operations.Setup(o => o.GetHoldTimeAsync(TestChannelId, incoming.Id, It.IsAny<CancellationToken>()))
                   .ReturnsAsync(3u);
        var packet = new AttributedErrorPacket(new byte[292], new byte[920]);
        var attribution = new RecordingAttributionService { ErrorPacket = packet };
        _operations.Setup(o => o.FailHtlcAsync(TestChannelId, incoming.Id, packet, It.IsAny<CancellationToken>()))
                   .Callback(() => _calls.Add("attributed fail"))
                   .Returns(Task.CompletedTask);
        var reason = Enumerable.Repeat((byte)0x0F, 292).ToArray();
        var downstream = Enumerable.Repeat((byte)0xAD, 920).ToArray();
        var removal = HtlcRemoval.Fail(reason, downstream);

        // Act
        await CreateSwitch(attribution, advertiseAttribution: true)
           .HandleAsync(new OutgoingHtlcFailed(s_otherChannelId, 4, incoming.PaymentHash, removal),
                        TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["attributed fail", "circuit Failed"], _calls);
        var call = Assert.Single(attribution.Calls);
        Assert.Equal("wrap error", call.Call);
        Assert.Equal(circuit.IncomingSharedSecret, call.Secret);
        Assert.Equal(reason, call.First);
        Assert.Equal(downstream, call.Second);
        Assert.Equal(3u, call.HoldTime);
    }

    private HtlcSwitch CreateSwitch(IAttributionDataService? attributionDataService = null,
                                    bool advertiseAttribution = false)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        var provider = services.BuildServiceProvider();
        var nodeOptions = new NodeOptions { EnableHtlcs = true };
        if (advertiseAttribution)
            nodeOptions.Features.OptionAttributionData = FeatureSupport.Optional;
        var options = Options.Create(nodeOptions);
        var onionProcessor = new IncomingOnionProcessor(new Mock<ISphinxService>().Object,
                                                        new Mock<IHopPayloadSerializer>().Object,
                                                        new Mock<IOnionReplayStore>().Object,
                                                        NullLogger<IncomingOnionProcessor>.Instance);
        return new HtlcSwitch(new ChannelLockProvider(), _context.ChannelMemoryRepository.Object, _operations.Object,
                              new Mock<IFailureOnionService>().Object,
                              new FinalHopProcessor(NullLogger<FinalHopProcessor>.Instance),
                              new HtlcForwardingPolicy(options), NullLogger<HtlcSwitch>.Instance, onionProcessor,
                              new Mock<IPeerLivenessProbe>().Object, provider.GetRequiredService<IServiceScopeFactory>(),
                              localPaymentHandlers: [_paymentHandler.Object], nodeOptions: options,
                              attributionDataService: attributionDataService);
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
                                           status == ForwardCircuitStatus.Pending ? (ChannelId?)null : s_otherChannelId,
                                           status == ForwardCircuitStatus.Pending ? null : 4UL,
                                           status is ForwardCircuitStatus.Fulfilled or ForwardCircuitStatus.Failed
                                               ? now
                                               : null);
    }

    /// <summary>Records the switch's wrap calls (Moq cannot match span arguments).</summary>
    private sealed class RecordingAttributionService : IAttributionDataService
    {
        public List<(string Call, Secret Secret, byte[] First, byte[] Second, uint HoldTime)> Calls { get; } = [];
        public AttributedErrorPacket? ErrorPacket { get; init; }
        public AttributedFulfillment? Fulfillment { get; init; }

        public AttributedErrorPacket CreateErrorPacket(Secret sharedSecret, FailureMessage message, uint holdTime,
                                                       int minFailurePadLength = 256) =>
            throw new NotSupportedException();

        public AttributedErrorPacket CreateErrorPacketFromMalformed(Secret incomingSharedSecret,
                                                                    FailureCode failureCode,
                                                                    ReadOnlySpan<byte> sha256OfOnion, uint holdTime,
                                                                    int minFailurePadLength = 256) =>
            throw new NotSupportedException();

        public AttributedErrorPacket WrapErrorPacket(Secret sharedSecret, ReadOnlySpan<byte> errorPacket,
                                                     ReadOnlySpan<byte> downstreamAttributionData, uint holdTime)
        {
            Calls.Add(("wrap error", sharedSecret, errorPacket.ToArray(), downstreamAttributionData.ToArray(),
                       holdTime));
            return ErrorPacket!;
        }

        public AttributedFailure DecryptErrorPacket(IReadOnlyList<Secret> hopSharedSecrets,
                                                    ReadOnlySpan<byte> errorPacket,
                                                    ReadOnlySpan<byte> attributionData) =>
            throw new NotSupportedException();

        public AttributedFulfillment CreateFulfillment(Secret sharedSecret, uint holdTime,
                                                       IReadOnlyList<BaseTlv>? fulfillmentRecords = null) =>
            throw new NotSupportedException();

        public AttributedFulfillment WrapFulfillment(Secret sharedSecret,
                                                     ReadOnlySpan<byte> downstreamAttributionData,
                                                     ReadOnlySpan<byte> downstreamFulfillmentPayload, uint holdTime)
        {
            Calls.Add(("wrap fulfillment", sharedSecret, downstreamAttributionData.ToArray(),
                       downstreamFulfillmentPayload.ToArray(), holdTime));
            return Fulfillment!;
        }

        public byte[] WrapFulfillmentPayload(Secret sharedSecret, ReadOnlySpan<byte> fulfillmentPayload) =>
            throw new NotSupportedException();

        public VerifiedFulfillment VerifyFulfillment(IReadOnlyList<Secret> hopSharedSecrets,
                                                     ReadOnlySpan<byte> attributionData,
                                                     ReadOnlySpan<byte> fulfillmentPayload) =>
            throw new NotSupportedException();
    }
}