using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Safety;

using Application.Channels.Safety;
using Application.Channels.Safety.Interfaces;
using Channels.Handlers;
using Domain.Bitcoin.Events;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Policies;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Services;

/// <summary>
/// BOLT2 plan N9-T2: <see cref="HtlcExpiryMonitor"/> on real commitment states (<see cref="RealSigningCommitmentPair"/>:
/// Alice offers, Bob receives). B2-CLTV-03 (offered past cltv_expiry + G fails the channel), B2-FWD-03 (unresolved
/// incoming failed back at cltv_expiry - delta through <see cref="IChannelOperations"/>), B2-CLTV-06 (fulfilled or
/// preimage-known incoming fails the channel at cltv_expiry - 18) and the forwarded case (never failed upstream while
/// the downstream HTLC is live).
/// </summary>
public sealed class HtlcExpiryMonitorTests : IDisposable
{
    private const uint Cltv = 600;
    private const uint Delta = 40; // RoutingOptions.CltvExpiryDelta default
    private const uint FulfillSafety = HtlcDeadlinePolicy.DefaultFulfillSafetyBlocks; // 18
    private const uint MinFinalCltv = 40; // RoutingOptions.InvoiceMinFinalCltvExpiry default

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly Mock<IBlockchainMonitor> _blockchainMonitor = new();
    private readonly Mock<IChannelFailureService> _failureService = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelOperations> _operations = new();
    private readonly Mock<IFailureOnionService> _failureOnion = new();
    private readonly Mock<IForwardCircuitDbRepository> _circuits = new();
    private readonly Mock<IChannelStateDbRepository> _stateDb = new();
    private readonly Mock<IInvoiceDbRepository> _invoices = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly ServiceProvider _provider;
    private readonly byte[] _errorPacket = [0xEE, 0x01];
    private ChannelModel _channel;

    public HtlcExpiryMonitorTests()
    {
        _channel = _pair.Alice.Channel;
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => predicate(_channel) ? [_channel] : []);
        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _failureService.Setup(f => f.FailChannelAsync(It.IsAny<ChannelId>(), It.IsAny<ChannelFailureRequest>(),
                                                      It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, null));
        _failureOnion.Setup(f => f.CreateErrorPacket(It.IsAny<Secret>(), It.IsAny<FailureMessage>(), It.IsAny<int>()))
                     .Returns(_errorPacket);
        _stateDb.Setup(r => r.GetOnionSharedSecretAsync(It.IsAny<ChannelId>(), It.IsAny<HtlcKey>()))
                .ReturnsAsync(new Secret(Enumerable.Repeat((byte)0x33, 32).ToArray()));
        _stateDb.Setup(r => r.FindHtlcsByOriginAsync(It.IsAny<HtlcOrigin>())).ReturnsAsync([]);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(_circuits.Object);
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(_stateDb.Object);
        unitOfWork.SetupGet(u => u.InvoiceDbRepository).Returns(_invoices.Object);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        _provider = services.BuildServiceProvider();
    }

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    [Fact]
    public async Task Given_OfferedHtlc_When_BlockReachesCltvPlusG_Then_ChannelFailedAndBroadcastAsked()
    {
        // Arrange: Alice offered an HTLC with cltv_expiry 600, locked in on both sides
        var id = _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Alice);
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv + 1, TestContext.Current.CancellationToken);
        _failureService.VerifyNoOtherCalls();
        await monitor.CheckAsync(Cltv + 2, TestContext.Current.CancellationToken);

        // Assert (B2-CLTV-03)
        _failureService.Verify(f => f.FailChannelAsync(_channel.ChannelId,
                                                       It.Is<ChannelFailureRequest>(r =>
                                                           r.Broadcast && r.RequirementId == "B2-CLTV-03"
                                                        && r.Reason.Contains($"HTLC {id}")),
                                                       It.IsAny<CancellationToken>()), Times.Once);
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ChannelFailedByMonitor_When_NextBlocks_Then_NotFailedAgainUnlessPublishFailed()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Alice);
        _failureService.SetupSequence(f => f.FailChannelAsync(It.IsAny<ChannelId>(),
                                                              It.IsAny<ChannelFailureRequest>(),
                                                              It.IsAny<CancellationToken>()))
                       .ReturnsAsync(new ChannelFailureOutcome(ChannelFailureStatus.PublishFailed, null))
                       .ReturnsAsync(new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, null));
        var monitor = CreateMonitor();

        // Act: the publish fails once (retried on the next block), then succeeds (never asked again)
        for (var height = Cltv + 2; height < Cltv + 6; height++)
            await monitor.CheckAsync(height, TestContext.Current.CancellationToken);

        // Assert
        _failureService.Verify(f => f.FailChannelAsync(It.IsAny<ChannelId>(), It.IsAny<ChannelFailureRequest>(),
                                                       It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_UnresolvedFinalHopHtlc_When_BlockReachesFulfillDeadline_Then_FailedBackUpstream()
    {
        // Arrange: Bob received Alice's HTLC and knows nothing about it (no invoice, no forward)
        var id = _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        var monitor = CreateMonitor();

        // Act: the forwarding distance does not apply to a never-forwarded HTLC
        await monitor.CheckAsync(Cltv - Delta, TestContext.Current.CancellationToken);
        await monitor.CheckAsync(Cltv - FulfillSafety - 1, TestContext.Current.CancellationToken);
        _operations.VerifyNoOtherCalls();
        await monitor.CheckAsync(Cltv - FulfillSafety, TestContext.Current.CancellationToken);

        // Assert (B2-CLTV-05): temporary_node_failure encrypted with the stored shared secret
        _operations.Verify(o => o.FailHtlcAsync(_channel.ChannelId, id,
                                                It.Is<ReadOnlyMemory<byte>>(r => r.ToArray().SequenceEqual(_errorPacket)),
                                                It.IsAny<CancellationToken>()), Times.Once);
        _failureOnion.Verify(f => f.CreateErrorPacket(It.IsAny<Secret>(),
                                                      It.Is<FailureMessage>(m =>
                                                          m.Code == Domain.Protocol.Onion.Enums.FailureCode
                                                             .TemporaryNodeFailure),
                                                      It.IsAny<int>()));
        _failureService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_FinalHopHtlcWithOpenInvoiceAndMinFinalCltv_When_NextBlocks_Then_NotFailedBack()
    {
        // Arrange: a payer's final HTLC, cltv_expiry = height + min_final_cltv_expiry + 3, not settled by the switch
        // yet (the invoice is still Open); the forwarding distance (40) would fail it back within 3 blocks
        const uint height = Cltv - MinFinalCltv - 3;
        var preimage = RealSigningCommitmentPair.Preimage(1);
        _pair.Add(_pair.Alice, 20_000_000, preimage, Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        _invoices.Setup(r => r.GetByPaymentHashAsync(It.IsAny<Hash>()))
                 .ReturnsAsync(Invoice(RealSigningCommitmentPair.Hash(preimage), InvoiceStatus.Open));
        var monitor = CreateMonitor();

        // Act
        for (var h = height; h < Cltv - FulfillSafety; h++)
            await monitor.CheckAsync(h, TestContext.Current.CancellationToken);

        // Assert: nothing before the fulfillment deadline
        _operations.VerifyNoOtherCalls();
        _failureService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_IncomingContinuedByOutgoingOriginWithoutCircuit_When_PastIncomingDeadlines_Then_NeverFailedBack()
    {
        // Arrange: an outgoing HTLC carries this HTLC as its origin (no preimage yet): the downstream decides
        var incomingId = _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        _stateDb.Setup(r => r.FindHtlcsByOriginAsync(HtlcOrigin.Forwarded(_channel.ChannelId, incomingId)))
                .ReturnsAsync([(_channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, 77))]);
        var monitor = CreateMonitor();

        // Act
        foreach (var h in new[] { Cltv - Delta, Cltv - FulfillSafety, Cltv })
            await monitor.CheckAsync(h, TestContext.Current.CancellationToken);

        // Assert
        _operations.VerifyNoOtherCalls();
        _failureService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_FailBackRefused_When_NextBlock_Then_Retried()
    {
        // Arrange: the peer is away the first time
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        _operations.SetupSequence(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(),
                                                       It.IsAny<ReadOnlyMemory<byte>>(),
                                                       It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new CommitmentRefusedException("B2-NO-02", "peer away"))
                   .Returns(Task.CompletedTask);
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - FulfillSafety, TestContext.Current.CancellationToken);
        await monitor.CheckAsync(Cltv - FulfillSafety + 1, TestContext.Current.CancellationToken);

        // Assert
        _operations.Verify(o => o.FailHtlcAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>(),
                                                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
                           Times.Exactly(2));
    }

    [Fact]
    public async Task Given_NoSharedSecretAndNoProcessor_When_FailBackDue_Then_NothingSent()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        _stateDb.Setup(r => r.GetOnionSharedSecretAsync(It.IsAny<ChannelId>(), It.IsAny<HtlcKey>()))
                .ReturnsAsync((Secret?)null);
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - FulfillSafety, TestContext.Current.CancellationToken);

        // Assert
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_SettledInvoiceForIncomingHtlc_When_Deadlines_Then_NeverFailedBackButChannelFailedAtFulfillDeadline()
    {
        // Arrange: we are the final hop, the switch committed the HTLC to its set (the preimage on its record) and
        // settled the invoice, but the fulfill never got committed
        var preimage = RealSigningCommitmentPair.Preimage(1);
        var id = _pair.Add(_pair.Alice, 20_000_000, preimage, Cltv);
        _pair.Settle(_pair.Alice);
        MarkPreimage(_pair.Bob, id, preimage);
        UseChannel(_pair.Bob);
        _invoices.Setup(r => r.GetByPaymentHashAsync(It.IsAny<Hash>()))
                 .ReturnsAsync(Invoice(RealSigningCommitmentPair.Hash(preimage), InvoiceStatus.Settled));
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - Delta, TestContext.Current.CancellationToken);
        await monitor.CheckAsync(Cltv - 19, TestContext.Current.CancellationToken);
        _failureService.VerifyNoOtherCalls();
        await monitor.CheckAsync(Cltv - 18, TestContext.Current.CancellationToken);

        // Assert (B2-CLTV-06)
        _operations.VerifyNoOtherCalls();
        _failureService.Verify(f => f.FailChannelAsync(_channel.ChannelId,
                                                       It.Is<ChannelFailureRequest>(r =>
                                                           r.RequirementId == "B2-CLTV-06"),
                                                       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_DuplicateHtlcForSettledInvoiceWhoseFailWasNotSent_When_FulfillDeadline_Then_FailedBackNotChannelFailed()
    {
        // Arrange (NL-337): the invoice was settled by another HTLC set; this HTLC for the same hash carries no mark
        // (not a part of the committed set), and the switch's 0x400F could not be sent while the peer was away
        var preimage = RealSigningCommitmentPair.Preimage(1);
        var id = _pair.Add(_pair.Alice, 20_000_000, preimage, Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        _invoices.Setup(r => r.GetByPaymentHashAsync(It.IsAny<Hash>()))
                 .ReturnsAsync(Invoice(RealSigningCommitmentPair.Hash(preimage), InvoiceStatus.Settled));
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - FulfillSafety - 1, TestContext.Current.CancellationToken);
        _operations.VerifyNoOtherCalls();
        await monitor.CheckAsync(Cltv - FulfillSafety, TestContext.Current.CancellationToken);

        // Assert (B2-CLTV-05): failed back upstream, the channel is never failed
        _operations.Verify(o => o.FailHtlcAsync(_channel.ChannelId, id, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Once);
        _failureService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(InvoiceStatus.Open)]
    [InlineData(InvoiceStatus.Accepted)]
    public async Task Given_MarkedHtlcOfAnInvoiceNotSettled_When_FulfillDeadline_Then_FailedBackNotChannelFailed(
        InvoiceStatus status)
    {
        // Arrange (NL-337, NL-323): a mark left by a set that never settled commits to nothing
        var preimage = RealSigningCommitmentPair.Preimage(1);
        var id = _pair.Add(_pair.Alice, 20_000_000, preimage, Cltv);
        _pair.Settle(_pair.Alice);
        MarkPreimage(_pair.Bob, id, preimage);
        UseChannel(_pair.Bob);
        _invoices.Setup(r => r.GetByPaymentHashAsync(It.IsAny<Hash>()))
                 .ReturnsAsync(Invoice(RealSigningCommitmentPair.Hash(preimage), status));
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - FulfillSafety, TestContext.Current.CancellationToken);

        // Assert
        _operations.Verify(o => o.FailHtlcAsync(_channel.ChannelId, id, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Once);
        _failureService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_MarkWithAnotherPreimageThanTheSettledInvoice_When_FulfillDeadline_Then_FailedBack()
    {
        // Arrange (NL-337): the invoice is Settled with another preimage than the one on the record
        var preimage = RealSigningCommitmentPair.Preimage(1);
        var id = _pair.Add(_pair.Alice, 20_000_000, preimage, Cltv);
        _pair.Settle(_pair.Alice);
        MarkPreimage(_pair.Bob, id, preimage);
        UseChannel(_pair.Bob);
        var settled = new InvoiceModel(RealSigningCommitmentPair.Hash(preimage), RealSigningCommitmentPair.Preimage(2),
                                       new Secret(new byte[32]), LightningMoney.MilliSatoshis(20_000_000), "test",
                                       "lnbcrt1test", DateTimeOffset.UtcNow, 3600, 40, InvoiceStatus.Settled,
                                       LightningMoney.MilliSatoshis(20_000_000), DateTimeOffset.UtcNow);
        _invoices.Setup(r => r.GetByPaymentHashAsync(It.IsAny<Hash>())).ReturnsAsync(settled);
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - FulfillSafety, TestContext.Current.CancellationToken);

        // Assert
        _operations.Verify(o => o.FailHtlcAsync(_channel.ChannelId, id, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Once);
        _failureService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_IncomingHtlcWeFulfilled_When_FulfillDeadline_Then_ChannelFailed()
    {
        // Arrange: Bob sent update_fulfill_htlc, the peer never committed it
        var preimage = RealSigningCommitmentPair.Preimage(1);
        var id = _pair.Add(_pair.Alice, 20_000_000, preimage, Cltv);
        _pair.Settle(_pair.Alice);
        _pair.Fulfill(_pair.Bob, id, preimage);
        UseChannel(_pair.Bob);
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - 19, TestContext.Current.CancellationToken);
        _failureService.VerifyNoOtherCalls();
        await monitor.CheckAsync(Cltv - 18, TestContext.Current.CancellationToken);

        // Assert
        _failureService.Verify(f => f.FailChannelAsync(_channel.ChannelId,
                                                       It.Is<ChannelFailureRequest>(r =>
                                                           r.RequirementId == "B2-CLTV-06"),
                                                       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ForwardedIncomingWithLiveDownstream_When_PastEveryIncomingDeadline_Then_NeverFailedUpstream()
    {
        // Arrange: Bob forwarded Alice's HTLC (circuit Offered); the downstream HTLC is still unresolved
        var incomingId = _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        _circuits.Setup(r => r.GetByIncomingAsync(_channel.ChannelId, incomingId))
                 .ReturnsAsync(Circuit(incomingId, ForwardCircuitStatus.Offered, outgoingHtlcId: 99));
        var monitor = CreateMonitor();

        // Act
        foreach (var height in new[] { Cltv - Delta, Cltv - 18, Cltv, Cltv + 2 })
            await monitor.CheckAsync(height, TestContext.Current.CancellationToken);

        // Assert
        _operations.VerifyNoOtherCalls();
        _failureService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ForwardedIncomingWhoseDownstreamWasFulfilled_When_FulfillDeadline_Then_ChannelFailed()
    {
        // Arrange: Alice → Bob (incoming) and Bob → Alice (the "downstream" of the same channel, fulfilled by Alice,
        // so Bob holds its preimage) while the upstream fulfill is not committed
        var preimage = RealSigningCommitmentPair.Preimage(1);
        var incomingId = _pair.Add(_pair.Alice, 20_000_000, preimage, Cltv);
        var outgoingId = _pair.Add(_pair.Bob, 19_000_000, preimage, Cltv - Delta);
        _pair.Settle(_pair.Alice);
        _pair.Fulfill(_pair.Alice, outgoingId, preimage);
        UseChannel(_pair.Bob);
        _stateDb.Setup(r => r.FindHtlcsByOriginAsync(HtlcOrigin.Forwarded(_channel.ChannelId, incomingId)))
                .ReturnsAsync([(_channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, outgoingId))]);
        _circuits.Setup(r => r.GetByIncomingAsync(_channel.ChannelId, incomingId))
                 .ReturnsAsync(Circuit(incomingId, ForwardCircuitStatus.Offered, outgoingId));
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - Delta + 1, TestContext.Current.CancellationToken);
        _operations.VerifyNoOtherCalls();
        await monitor.CheckAsync(Cltv - 18, TestContext.Current.CancellationToken);

        // Assert: the incoming HTLC's preimage is known, so it is never failed back, and the channel goes on chain
        _operations.VerifyNoOtherCalls();
        _failureService.Verify(f => f.FailChannelAsync(_channel.ChannelId,
                                                       It.Is<ChannelFailureRequest>(r =>
                                                           r.RequirementId == "B2-CLTV-06"),
                                                       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_OfferedCircuitOnAClosedOutgoingChannelWithoutPreimage_When_FailBackDue_Then_FailedBack()
    {
        // Arrange (NL-320 review): Bob forwarded Alice's HTLC over another channel that has closed on chain (Closed,
        // no longer loaded) without a preimage; the switch fails such a forward only when the lock-in is replayed, and
        // the link to Alice stays up
        var incomingId = _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        var closed = new NormalOperationTestContext(state: ChannelState.Closed).Channel;
        Assert.NotEqual(_channel.ChannelId, closed.ChannelId);
        _channelDb.Setup(r => r.GetByIdAsync(closed.ChannelId)).ReturnsAsync(closed);
        _circuits.Setup(r => r.GetByIncomingAsync(_channel.ChannelId, incomingId))
                 .ReturnsAsync(Circuit(incomingId, ForwardCircuitStatus.Offered, 7, closed.ChannelId));
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - Delta - 1, TestContext.Current.CancellationToken);
        _operations.VerifyNoOtherCalls();
        await monitor.CheckAsync(Cltv - Delta, TestContext.Current.CancellationToken);

        // Assert (B2-FWD-03): failed back at the fail-back deadline, the channel is not failed
        _operations.Verify(o => o.FailHtlcAsync(_channel.ChannelId, incomingId, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Once);
        _failureService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(ChannelState.OnchainResolving)]
    [InlineData(ChannelState.Failed)]
    public async Task Given_OfferedCircuitOnAnUnloadedOutgoingChannelNotClosed_When_PastEveryIncomingDeadline_Then_NeverFailedBack(
        ChannelState state)
    {
        // Arrange (NL-320 review): only a Closed outgoing channel can no longer resolve the forward
        var incomingId = _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        var outgoing = new NormalOperationTestContext(state: state).Channel;
        _channelDb.Setup(r => r.GetByIdAsync(outgoing.ChannelId)).ReturnsAsync(outgoing);
        _circuits.Setup(r => r.GetByIncomingAsync(_channel.ChannelId, incomingId))
                 .ReturnsAsync(Circuit(incomingId, ForwardCircuitStatus.Offered, 7, outgoing.ChannelId));
        var monitor = CreateMonitor();

        // Act
        foreach (var height in new[] { Cltv - Delta, Cltv - 18, Cltv })
            await monitor.CheckAsync(height, TestContext.Current.CancellationToken);

        // Assert
        _operations.VerifyNoOtherCalls();
        _failureService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_FailedCircuit_When_FailBackDue_Then_FailedBack()
    {
        // Arrange: the forward failed (refused offer or irrevocable downstream failure)
        var incomingId = _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        _circuits.Setup(r => r.GetByIncomingAsync(_channel.ChannelId, incomingId))
                 .ReturnsAsync(Circuit(incomingId, ForwardCircuitStatus.Failed, outgoingHtlcId: null));
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - Delta, TestContext.Current.CancellationToken);

        // Assert
        _operations.Verify(o => o.FailHtlcAsync(_channel.ChannelId, incomingId, It.IsAny<ReadOnlyMemory<byte>>(),
                                                It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_FailedChannel_When_UnresolvedIncomingDue_Then_NotFailedBack()
    {
        // Arrange: nothing can be sent on a failed channel
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Bob);
        _channel.UpdateState(ChannelState.Failed);
        var monitor = CreateMonitor();

        // Act
        await monitor.CheckAsync(Cltv - FulfillSafety, TestContext.Current.CancellationToken);

        // Assert
        _operations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_Started_When_NewBlockEvent_Then_RoundRunsAndStopUnsubscribes()
    {
        // Arrange
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), Cltv);
        _pair.Settle(_pair.Alice);
        UseChannel(_pair.Alice);
        var monitor = CreateMonitor();
        monitor.Start();

        // Act
        _blockchainMonitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(Cltv + 2, new byte[32]));
        await monitor.WhenIdleAsync();
        await monitor.StopAsync();
        _blockchainMonitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(Cltv + 3, new byte[32]));
        await monitor.WhenIdleAsync();

        // Assert
        _failureService.Verify(f => f.FailChannelAsync(It.IsAny<ChannelId>(), It.IsAny<ChannelFailureRequest>(),
                                                       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void Given_SafetyServices_When_Registered_Then_Resolvable()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(_blockchainMonitor.Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton(_operations.Object);
        services.AddSingleton(_failureOnion.Object);
        services.AddSingleton(new Mock<IChannelLockProvider>().Object);
        services.AddSingleton(new Mock<Domain.Bitcoin.Interfaces.ILightningSigner>().Object);
        services.AddSingleton(new Mock<Domain.Bitcoin.Transactions.Interfaces.ICommitmentTransactionModelFactory>()
                                 .Object);
        services.AddSingleton(new Mock<Infrastructure.Bitcoin.Builders.Interfaces.ICommitmentTransactionBuilder>()
                                 .Object);

        // Act
        services.AddChannelSafetyServices();
        services.AddChannelSafetyServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        // Assert: one instance behind each interface
        Assert.Same(provider.GetRequiredService<ChannelFailureService>(),
                    provider.GetRequiredService<IChannelFailureService>());
        Assert.Same(provider.GetRequiredService<HtlcExpiryMonitor>(), provider.GetRequiredService<IHtlcExpiryMonitor>());
        Assert.IsType<PeerChannelErrorSender>(provider.GetRequiredService<IChannelErrorSender>());
        Assert.Equal(Delta, provider.GetRequiredService<HtlcExpiryMonitor>().Policy.FailBackBlocks);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private void UseChannel(RealSigningNode node)
    {
        node.Channel.UpdateCommitments(node.State);
        _channel = node.Channel;
    }

    /// <summary>What the switch's <c>MarkPartAsync</c> persists: the preimage on the incoming HTLC's record.</summary>
    private static void MarkPreimage(RealSigningNode node, ulong htlcId, Secret preimage)
    {
        var state = node.State;
        var record = state.GetHtlc(HtlcDirection.Incoming, htlcId)! with { KnownPreimage = preimage };
        node.State = ChannelCommitments.Restore(state.ChannelId, state.Params, state.LocalBalanceMsat,
                                                state.RemoteBalanceMsat, state.Htlcs.SetItem(record.Key, record).Values,
                                                state.FeeUpdates, state.LocalNextHtlcId, state.RemoteNextHtlcId,
                                                state.LocalCommit, state.RemoteCommit, state.RemoteNextCommit,
                                                state.RemoteNextPerCommitmentPoint);
    }

    private HtlcExpiryMonitor CreateMonitor() =>
        new(_blockchainMonitor.Object, _failureService.Object, _memory.Object, _operations.Object,
            _failureOnion.Object, NullLogger<HtlcExpiryMonitor>.Instance, Options.Create(new NodeOptions()),
            _provider.GetRequiredService<IServiceScopeFactory>());

    private ForwardCircuitModel Circuit(ulong incomingId, ForwardCircuitStatus status, ulong? outgoingHtlcId,
                                        ChannelId? outgoingChannelId = null) =>
        ForwardCircuitModel.Restore(_channel.ChannelId, incomingId, LightningMoney.MilliSatoshis(20_000_000), Cltv,
                                    new Hash(new byte[32]), new Secret(new byte[32]), new ShortChannelId(1, 2, 3),
                                    LightningMoney.MilliSatoshis(19_000_000), Cltv - Delta, DateTimeOffset.UnixEpoch,
                                    status,
                                    outgoingHtlcId is null ? (ChannelId?)null : outgoingChannelId ?? _channel.ChannelId,
                                    outgoingHtlcId,
                                    status is ForwardCircuitStatus.Failed or ForwardCircuitStatus.Fulfilled
                                        ? DateTimeOffset.UnixEpoch
                                        : null);

    private static InvoiceModel Invoice(Hash hash, InvoiceStatus status) =>
        new(hash, RealSigningCommitmentPair.Preimage(1), new Secret(new byte[32]),
            LightningMoney.MilliSatoshis(20_000_000), "test", "lnbcrt1test", DateTimeOffset.UtcNow, 3600, 40, status,
            status == InvoiceStatus.Open ? null : LightningMoney.MilliSatoshis(20_000_000),
            status == InvoiceStatus.Settled ? DateTimeOffset.UtcNow : null);
}