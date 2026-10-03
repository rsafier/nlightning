using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Channels.Interfaces;
using Application.Channels.Services;
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
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.Interfaces;
using Domain.Serialization.Interfaces;
using static Channels.Handlers.NormalOperationTestContext;

/// <summary>
/// NL-875 (TR3-P3): <see cref="HtlcSwitch"/> routes the events of trampoline relays (<c>HtlcOrigin.Trampoline</c>,
/// incoming relay parts) to the <see cref="ITrampolineHtlcHandler"/> only, never to the local-payment handlers, and
/// keeps the archived outgoing record until the relay is done.
/// </summary>
public class TrampolineOriginSwitchTests
{
    private static readonly ChannelId s_outgoingChannelId = new(Enumerable.Repeat((byte)0x99, 32).ToArray());

    private readonly NormalOperationTestContext _context = new();
    private readonly Mock<IChannelOperations> _operations = new();
    private readonly Mock<IForwardCircuitDbRepository> _circuits = new();
    private readonly Mock<ITrampolineRelayDbRepository> _relays = new();
    private readonly Mock<ILocalPaymentHtlcHandler> _paymentHandler = new();
    private readonly Mock<ITrampolineHtlcHandler> _trampolineHandler = new();
    private readonly List<string> _calls = [];
    private readonly Hash _relayHash = HashOf(SecretOf(7));

    public TrampolineOriginSwitchTests()
    {
        _context.UnitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(_circuits.Object);
        _context.UnitOfWork.SetupGet(u => u.TrampolineRelayDbRepository).Returns(_relays.Object);
        _context.ChannelStateDbRepository
                .Setup(r => r.PruneSettledHtlcsAsync(It.IsAny<ChannelId>(), It.IsAny<IEnumerable<HtlcKey>>()))
                .Callback(() => _calls.Add("prune"))
                .Returns(Task.CompletedTask);
        _context.ChannelStateDbRepository
                .Setup(r => r.GetHtlcOriginAsync(s_outgoingChannelId, new HtlcKey(HtlcDirection.Outgoing, 4)))
                .ReturnsAsync(HtlcOrigin.Trampoline(_relayHash));
        _trampolineHandler.Setup(h => h.HandleSettledAsync(It.IsAny<OutgoingHtlcSettled>(), It.IsAny<Hash>(),
                                                           It.IsAny<CancellationToken>()))
                          .ReturnsAsync(true);
    }

    [Fact]
    public async Task Given_ATrampolineOutgoingHtlc_When_Fulfilled_Then_OnlyTheTrampolineHandlerIsCalled()
    {
        // Arrange
        var fulfilled = new OutgoingHtlcFulfilled(s_outgoingChannelId, 4, _relayHash, SecretOf(7));

        // Act
        await CreateSwitch().HandleAsync(fulfilled, TestContext.Current.CancellationToken);

        // Assert
        _trampolineHandler.Verify(h => h.HandleFulfilledAsync(fulfilled, _relayHash, It.IsAny<CancellationToken>()),
                                  Times.Once);
        _paymentHandler.VerifyNoOtherCalls();
        _operations.VerifyNoOtherCalls();
        _circuits.Verify(c => c.UpdateAsync(It.IsAny<ForwardCircuitModel>()), Times.Never);
    }

    [Fact]
    public async Task Given_ATrampolineOutgoingHtlc_When_Failed_Then_OnlyTheTrampolineHandlerIsCalled()
    {
        // Arrange
        var failed = new OutgoingHtlcFailed(s_outgoingChannelId, 4, _relayHash, HtlcRemoval.Fail(new byte[] { 1 }));

        // Act
        await CreateSwitch().HandleAsync(failed, TestContext.Current.CancellationToken);

        // Assert
        _trampolineHandler.Verify(h => h.HandleFailedAsync(failed, _relayHash, It.IsAny<CancellationToken>()),
                                  Times.Once);
        _paymentHandler.VerifyNoOtherCalls();
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_NoTrampolineHandler_When_ATrampolineHtlcResolvesAndSettles_Then_NothingIsDoneAndTheRowIsKept()
    {
        // Arrange
        UseRelay(TrampolineRelayStatus.Fulfilled);
        var htlcSwitch = CreateSwitch(withTrampolineHandler: false);

        // Act
        await htlcSwitch.HandleAsync(new OutgoingHtlcFulfilled(s_outgoingChannelId, 4, _relayHash, SecretOf(7)),
                                     TestContext.Current.CancellationToken);
        await htlcSwitch.HandleAsync(Settled(), TestContext.Current.CancellationToken);

        // Assert
        _paymentHandler.VerifyNoOtherCalls();
        _operations.VerifyNoOtherCalls();
        Assert.DoesNotContain("prune", _calls);
    }

    [Fact]
    public async Task Given_ACompletedRelayWhosePartsAreRemoved_When_ItsOutgoingHtlcSettles_Then_TheRowIsPruned()
    {
        // Arrange - the relay's incoming part (HTLC 9) is no longer in the loaded channel
        UseRelay(TrampolineRelayStatus.Fulfilled);

        // Act
        await CreateSwitch().HandleAsync(Settled(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["prune"], _calls);
        _paymentHandler.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ARelayThatIsNotCompleted_When_ItsOutgoingHtlcSettles_Then_TheRowIsKept()
    {
        // Arrange - the engine may still try another attempt (Sending)
        UseRelay(TrampolineRelayStatus.Sending);

        // Act
        await CreateSwitch().HandleAsync(Settled(), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("prune", _calls);
    }

    [Fact]
    public async Task Given_ACompletedRelayWithAPartStillLockedIn_When_ItsOutgoingHtlcSettles_Then_TheRowIsKept()
    {
        // Arrange - the part's upstream fulfill was refused (peer away): the outgoing record keeps the preimage
        var incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(7));
        UseRelay(TrampolineRelayStatus.Fulfilled, incoming.Id);

        // Act
        await CreateSwitch().HandleAsync(Settled(), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("prune", _calls);
    }

    [Fact]
    public async Task Given_AHandlerThatKeepsTheRecord_When_ItsOutgoingHtlcSettles_Then_TheRowIsKept()
    {
        // Arrange
        UseRelay(TrampolineRelayStatus.Fulfilled);
        _trampolineHandler.Setup(h => h.HandleSettledAsync(It.IsAny<OutgoingHtlcSettled>(), It.IsAny<Hash>(),
                                                           It.IsAny<CancellationToken>()))
                          .ReturnsAsync(false);

        // Act
        await CreateSwitch().HandleAsync(Settled(), TestContext.Current.CancellationToken);

        // Assert
        Assert.DoesNotContain("prune", _calls);
    }

    [Fact]
    public async Task Given_AHandlerThatFailed_When_TheRelaySettles_Then_TheRowIsKeptUntilAHandledReplay()
    {
        // Arrange
        UseRelay(TrampolineRelayStatus.Failed);
        var failed = new OutgoingHtlcFailed(s_outgoingChannelId, 4, _relayHash, HtlcRemoval.Fail(new byte[] { 1 }));
        _trampolineHandler.SetupSequence(h => h.HandleFailedAsync(failed, _relayHash, It.IsAny<CancellationToken>()))
                          .ThrowsAsync(new InvalidOperationException("database down"))
                          .Returns(Task.CompletedTask);
        var htlcSwitch = CreateSwitch();

        // Act
        await htlcSwitch.HandleAsync(failed, TestContext.Current.CancellationToken);
        await htlcSwitch.HandleAsync(Settled(), TestContext.Current.CancellationToken);
        var prunedFirst = _calls.Contains("prune");
        await htlcSwitch.HandleAsync(failed, TestContext.Current.CancellationToken);
        await htlcSwitch.HandleAsync(Settled(), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(prunedFirst);
        Assert.Equal(["prune"], _calls);
    }

    [Fact]
    public async Task Given_AnIncomingHtlcThatIsARelayPart_When_ItsLockInIsReplayed_Then_TheHandlerResumesItAndNothingIsPeeled()
    {
        // Arrange
        var incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(7));
        var part = UseRelay(TrampolineRelayStatus.Sending, incoming.Id);
        var lockedIn = new IncomingHtlcLockedIn(TestChannelId, incoming);
        var sphinx = new Mock<ISphinxService>();

        // Act
        await CreateSwitch(sphinx: sphinx.Object).HandleAsync(lockedIn, TestContext.Current.CancellationToken);

        // Assert - neither peeled again nor failed: the relay decides
        _trampolineHandler.Verify(h => h.HandleIncomingPartLockedInAsync(lockedIn, part,
                                                                          It.IsAny<CancellationToken>()), Times.Once);
        sphinx.VerifyNoOtherCalls();
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_NoTrampolineHandler_When_ARelayPartsLockInIsReplayed_Then_ItIsNeitherPeeledNorFailed()
    {
        // Arrange
        var incoming = _context.LockIn(HtlcDirection.Incoming, 30_000_000, SecretOf(7));
        UseRelay(TrampolineRelayStatus.Collecting, incoming.Id);
        var sphinx = new Mock<ISphinxService>();

        // Act
        await CreateSwitch(withTrampolineHandler: false, sphinx: sphinx.Object)
           .HandleAsync(new IncomingHtlcLockedIn(TestChannelId, incoming), TestContext.Current.CancellationToken);

        // Assert
        sphinx.VerifyNoOtherCalls();
        _operations.VerifyNoOtherCalls();
    }

    private TrampolineRelayPartModel UseRelay(TrampolineRelayStatus status, ulong partHtlcId = 9)
    {
        var relay = new TrampolineRelayModel(_relayHash, Point(0x0B), LightningMoney.MilliSatoshis(29_000_000), 560,
                                             LightningMoney.MilliSatoshis(30_000_000), DateTimeOffset.UnixEpoch);
        if (status is TrampolineRelayStatus.Sending or TrampolineRelayStatus.Fulfilled)
            relay.MarkSending();
        if (status == TrampolineRelayStatus.Fulfilled)
            relay.MarkFulfilled(SecretOf(7), LightningMoney.MilliSatoshis(1_000_000), DateTimeOffset.UnixEpoch);
        if (status == TrampolineRelayStatus.Failed)
            relay.MarkFailed(0x2002, "no route", DateTimeOffset.UnixEpoch);

        var part = new TrampolineRelayPartModel(_relayHash, TestChannelId, partHtlcId,
                                                LightningMoney.MilliSatoshis(30_000_000), 600, SecretOf(0x31),
                                                SecretOf(0x32), null);
        _relays.Setup(r => r.GetAsync(_relayHash)).ReturnsAsync((relay, [part]));
        _relays.Setup(r => r.GetPartAsync(TestChannelId, partHtlcId)).ReturnsAsync(part);
        return part;
    }

    private OutgoingHtlcSettled Settled() => new(s_outgoingChannelId, 4, _relayHash, HtlcRemovalKind.Fulfill);

    private HtlcSwitch CreateSwitch(bool withTrampolineHandler = true, ISphinxService? sphinx = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _context.UnitOfWork.Object);
        var provider = services.BuildServiceProvider();
        var nodeOptions = new NodeOptions { EnableHtlcs = true };
        nodeOptions.Features.OptionAttributionData = FeatureSupport.No;
        var options = Options.Create(nodeOptions);
        var onionProcessor = new IncomingOnionProcessor(sphinx ?? new UnreadableOnionSphinx(),
                                                        new Mock<IHopPayloadSerializer>().Object,
                                                        new Mock<IOnionReplayStore>().Object,
                                                        NullLogger<IncomingOnionProcessor>.Instance);
        return new HtlcSwitch(new ChannelLockProvider(), _context.ChannelMemoryRepository.Object, _operations.Object,
                              new Mock<IFailureOnionService>().Object,
                              new FinalHopProcessor(NullLogger<FinalHopProcessor>.Instance),
                              new HtlcForwardingPolicy(options), NullLogger<HtlcSwitch>.Instance, onionProcessor,
                              new Mock<IPeerLivenessProbe>().Object, provider.GetRequiredService<IServiceScopeFactory>(),
                              localPaymentHandlers: [_paymentHandler.Object], nodeOptions: options,
                              trampolineHandler: withTrampolineHandler ? _trampolineHandler.Object : null);
    }
}