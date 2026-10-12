using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Moq;

namespace NLightning.LndGrpc.Tests;

using Application.Payments.Routing;
using Application.Payments.Send;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using LndGrpc.Macaroons;
using LndGrpc.Services;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Routerrpc;
using Route = Testing.Lnd.Lnrpc.Route;

/// <summary>QueryRoutes and SendToRouteV2 as bos/ln-service drive them (NL-1242).</summary>
public partial class LndGrpcHostTests
{
    private static CompactPubKey RealKey()
    {
        using var key = new NBitcoin.Key();
        return new CompactPubKey(key.PubKey.ToBytes());
    }

    [Fact]
    public async Task Given_AnLndQuery_When_QueryRoutes_Then_ItsRestrictionsReachThePlannerAndTheRouteIsLnds()
    {
        // Arrange: our channel 7 to its peer, then the last hop, then the destination
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var destination = RealKey();
        var lastHop = RealKey();
        var ignored = RealKey();
        var hintNode = RealKey();
        var peer = channel.RemoteNodeId;
        var route = new PaymentRoute(
            [
                new RouteHop(peer, LightningMoney.MilliSatoshis(50_002_000), 700, new ShortChannelId(160, 1, 1)),
                new RouteHop(lastHop, LightningMoney.MilliSatoshis(50_000_123), 646, new ShortChannelId(170, 2, 0)),
                new RouteHop(destination, LightningMoney.MilliSatoshis(50_000_123), 646, null)
            ], LightningMoney.MilliSatoshis(50_003_000), 740, new Hash(new byte[32]), new Secret(new byte[32]));
        RouteQueryRequest? captured = null;
        _routeQuery.Setup(x => x.QueryRouteAsync(It.IsAny<RouteQueryRequest>(), It.IsAny<CancellationToken>()))
                   .Callback((RouteQueryRequest query, CancellationToken _) => captured = query)
                   .ReturnsAsync(new RouteQuote(route,
                                                new LocalChannelCandidate(channel.ChannelId, peer,
                                                                          new ShortChannelId(150, 7, 0)), 0.36, 600,
                                                "graph"));
        var request = new QueryRoutesRequest
        {
            PubKey = destination.ToString(),
            AmtMsat = 50_000_123,
            FinalCltvDelta = 46,
            FeeLimit = new FeeLimit { FixedMsat = 5_000 },
            CltvLimit = 500,
            LastHopPubkey = ByteString.CopyFrom((byte[])lastHop),
            UseMissionControl = true
        };
        request.IgnoredNodes.Add(ByteString.CopyFrom((byte[])ignored));
        request.IgnoredPairs.Add(new NodePair
        {
            From = ByteString.CopyFrom((byte[])peer),
            To = ByteString.CopyFrom((byte[])lastHop)
        });
        request.OutgoingChanIds.Add(LightningService.ToChanId(new ShortChannelId(150, 7, 0)));
        var hint = new RouteHint();
        hint.HopHints.Add(new HopHint
        {
            NodeId = hintNode.ToString(),
            ChanId = LightningService.ToChanId(new ShortChannelId(180, 3, 1)),
            FeeBaseMsat = 1,
            FeeProportionalMillionths = 2,
            CltvExpiryDelta = 40
        });
        request.RouteHints.Add(hint);
        request.DestCustomRecords.Add(65_537, ByteString.CopyFromUtf8("hi"));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.LightningClient.QueryRoutesAsync(request, cancellationToken: Ct);

        // Assert: the planner got every restriction
        Assert.NotNull(captured);
        Assert.Equal(destination, captured.Payee);
        Assert.Equal(50_000_123ul, captured.Amount.MilliSatoshi);
        Assert.Equal(5_000ul, captured.MaxFee.MilliSatoshi);
        Assert.Equal((ushort)46, captured.FinalCltvDelta);
        Assert.Equal(500u, captured.MaxTotalCltvDelta);
        Assert.Equal(lastHop, captured.LastHop);
        Assert.Equal([ignored], captured.IgnoredNodes);
        Assert.Equal([(peer, lastHop)], captured.IgnoredPairs);
        Assert.Equal([new ShortChannelId(150, 7, 0)], captured.OutgoingChannels!);
        var hintEntry = Assert.Single(Assert.Single(captured.RouteHints));
        Assert.Equal((hintNode, new ShortChannelId(180, 3, 1), 1u, 2u, (ushort)40),
                     (hintEntry.CompactPubKey, hintEntry.ShortChannelId, hintEntry.FeeBaseMsat,
                      hintEntry.FeeProportionalMillionths, hintEntry.CltvExpiryDelta));
        Assert.True(captured.UseMissionControl);

        // Assert: LND's Route, hop i reached over chan_id i, its fee and the expiry of the HTLC it offers next
#pragma warning disable CS0612 // the deprecated sat, capacity and tlv_payload fields ln-service still reads
        Assert.Equal(0.36, response.SuccessProb);
        var lnd = Assert.Single(response.Routes);
        Assert.Equal(740u, lnd.TotalTimeLock);
        Assert.Equal(50_003_000, lnd.TotalAmtMsat);
        Assert.Equal(50_003, lnd.TotalAmt);
        Assert.Equal(2_877, lnd.TotalFeesMsat);
        Assert.Equal(3, lnd.Hops.Count);
        Assert.Equal((new ShortChannelId(150, 7, 0), peer.ToString(), 50_002_000L, 1_000L, 700u, 1_000_000L),
                     (new ShortChannelId(lnd.Hops[0].ChanId), lnd.Hops[0].PubKey, lnd.Hops[0].AmtToForwardMsat,
                      lnd.Hops[0].FeeMsat, lnd.Hops[0].Expiry, lnd.Hops[0].ChanCapacity));
        Assert.Equal((new ShortChannelId(160, 1, 1), lastHop.ToString(), 50_000_123L, 1_877L, 646u),
                     (new ShortChannelId(lnd.Hops[1].ChanId), lnd.Hops[1].PubKey, lnd.Hops[1].AmtToForwardMsat,
                      lnd.Hops[1].FeeMsat, lnd.Hops[1].Expiry));
        Assert.Equal((new ShortChannelId(170, 2, 0), destination.ToString(), 50_000_123L, 0L, 646u),
                     (new ShortChannelId(lnd.Hops[2].ChanId), lnd.Hops[2].PubKey, lnd.Hops[2].AmtToForwardMsat,
                      lnd.Hops[2].FeeMsat, lnd.Hops[2].Expiry));
        Assert.All(lnd.Hops, h => Assert.True(h.TlvPayload));
        Assert.Null(lnd.Hops[2].MppRecord);
        Assert.Equal("hi", lnd.Hops[2].CustomRecords[65_537].ToStringUtf8());
#pragma warning restore CS0612
    }

    [Fact]
    public async Task Given_NoRoute_When_QueryRoutes_Then_LndsNoPathError()
    {
        // Arrange
        _routeQuery.Setup(x => x.QueryRouteAsync(It.IsAny<RouteQueryRequest>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InvalidOperationException("No route to the payee: nothing"));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.QueryRoutesAsync(new QueryRoutesRequest
            {
                PubKey = RealKey().ToString(),
                Amt = 1_000
            }, cancellationToken: Ct).ResponseAsync);
        var zero = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.QueryRoutesAsync(new QueryRoutesRequest
            {
                PubKey = RealKey().ToString()
            }, cancellationToken: Ct).ResponseAsync);

        // Assert: ln-service reads exactly this text as "no route"
        Assert.Equal(StatusCode.Unknown, error.StatusCode);
        Assert.Equal("unable to find a path to destination", error.Status.Detail);
        Assert.Equal(StatusCode.InvalidArgument, zero.StatusCode);
    }

    [Fact]
    public void Given_NoFeeLimit_When_Defaulted_Then_LndsHundredPercentUpTo1000SatAndFivePercentAbove()
    {
        // Act / Assert
        Assert.Equal(1_000_000ul, LightningService.FeeLimitFor(null, LightningMoney.Satoshis(1_000)).MilliSatoshi);
        Assert.Equal(100_000ul, LightningService.FeeLimitFor(null, LightningMoney.Satoshis(2_000)).MilliSatoshi);
        Assert.Equal(200_000ul, LightningService.FeeLimitFor(new LndGrpc.Lnrpc.FeeLimit { Percent = 10 },
                                                             LightningMoney.Satoshis(2_000)).MilliSatoshi);
        Assert.Equal(7_000ul, LightningService.FeeLimitFor(new LndGrpc.Lnrpc.FeeLimit { Fixed = 7 },
                                                           LightningMoney.Satoshis(2_000)).MilliSatoshi);
    }

    [Fact]
    public async Task Given_AQueriedRouteWithAnMppRecord_When_SendToRouteV2_Then_PayrouteGetsItAndThePreimageComesBack()
    {
        // Arrange
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var destination = RealKey();
        var preimage = new Secret(Enumerable.Repeat((byte)0x31, 32).ToArray());
        var hash = new Hash(SHA256.HashData((ReadOnlySpan<byte>)preimage));
        var payment = new PaymentModel(hash, null, destination, LightningMoney.MilliSatoshis(10_000),
                                       LightningMoney.MilliSatoshis(1_000), DateTimeOffset.UtcNow);
        payment.Succeed(preimage, DateTimeOffset.UtcNow);
        PayRouteRequest? captured = null;
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .Callback((PayRouteRequest request, PayInvoiceOptions _, CancellationToken _) =>
                                     captured = request)
                       .ReturnsAsync(new PayRouteResult(payment,
                                                        [new RouteOutcome(0, PaymentPartState.Succeeded, 4, null,
                                                                          null, null)]));
        var route = TwoHopRoute(channel.RemoteNodeId, destination);
        route.Hops[1].MppRecord = new MPPRecord
        {
            PaymentAddr = ByteString.CopyFrom(Enumerable.Repeat((byte)0x55, 32).ToArray()),
            TotalAmtMsat = 10_000
        };
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var attempt = await connection.RouterClient.SendToRouteV2Async(new SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])hash),
            Route = route
        }, cancellationToken: Ct);

        // Assert: our channel 7 first, hop i forwarding over hop i+1's chan_id, the mpp record as secret and total
        Assert.NotNull(captured);
        Assert.Equal(hash, captured.PaymentHash);
        Assert.Equal(Enumerable.Repeat((byte)0x55, 32).ToArray(), (byte[])captured.PaymentSecret!.Value);
        Assert.Equal(10_000ul, captured.TotalAmount!.MilliSatoshi);
        var supplied = Assert.Single(captured.Routes);
        Assert.Equal(channel.ChannelId, supplied.FirstHopChannelId);
        Assert.Equal(11_000ul, supplied.FirstHopAmount.MilliSatoshi);
        Assert.Equal(300u, supplied.FirstHopCltvExpiry);
        Assert.Equal((channel.RemoteNodeId, new ShortChannelId(160, 1, 1), 10_000ul, 260u),
                     (supplied.Hops[0].NodeId, supplied.Hops[0].OutgoingShortChannelId!.Value,
                      supplied.Hops[0].AmountToForward.MilliSatoshi, supplied.Hops[0].OutgoingCltvValue));
        Assert.Null(supplied.Hops[1].OutgoingShortChannelId);
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Succeeded, attempt.Status);
        Assert.Equal((byte[])preimage, attempt.Preimage.ToByteArray());
        Assert.Equal(4ul, attempt.AttemptId);
    }

    [Fact]
    public async Task Given_TheDestinationRefusesAProbe_When_SendToRouteV2_Then_TheFailureIsAtLndsDestinationIndex()
    {
        // Arrange: a probe (random hash, no mpp record) failed by the payee, our index 1 of 2 hops
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var destination = RealKey();
        var hash = new Hash(RandomNumberGenerator.GetBytes(32));
        var payment = new PaymentModel(hash, null, destination, LightningMoney.MilliSatoshis(10_000),
                                       LightningMoney.Zero, DateTimeOffset.UtcNow);
        PayRouteRequest? captured = null;
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .Callback((PayRouteRequest request, PayInvoiceOptions _, CancellationToken _) =>
                                     captured = request)
                       .ReturnsAsync(new PayRouteResult(payment,
                                                        [
                                                            new RouteOutcome(0, PaymentPartState.Failed, 5,
                                                                             FailureCode
                                                                                .IncorrectOrUnknownPaymentDetails,
                                                                             1, "unknown hash")
                                                        ]));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var attempt = await connection.RouterClient.SendToRouteV2Async(new SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])hash),
            Route = TwoHopRoute(channel.RemoteNodeId, destination)
        }, cancellationToken: Ct);

        // Assert: bos stops probing when the index is the hop count
        Assert.Null(captured!.PaymentSecret);
        Assert.Null(captured.KeysendPreimage);
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Failed, attempt.Status);
        Assert.Equal(Failure.Types.FailureCode.IncorrectOrUnknownPaymentDetails, attempt.Failure.Code);
        Assert.Equal(2u, attempt.Failure.FailureSourceIndex);
    }

    [Fact]
    public async Task Given_AKeysendRecord_When_SendToRouteV2_Then_ItIsAKeysendRouteWithTheOtherRecords()
    {
        // Arrange
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var destination = RealKey();
        var preimage = Enumerable.Repeat((byte)0x77, 32).ToArray();
        var hash = new Hash(SHA256.HashData(preimage));
        var payment = new PaymentModel(hash, null, destination, LightningMoney.MilliSatoshis(10_000),
                                       LightningMoney.Zero, DateTimeOffset.UtcNow);
        PayRouteRequest? captured = null;
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .Callback((PayRouteRequest request, PayInvoiceOptions _, CancellationToken _) =>
                                     captured = request)
                       .ReturnsAsync(new PayRouteResult(payment,
                                                        [new RouteOutcome(0, PaymentPartState.Failed, null, null,
                                                                          null, "refused")]));
        var route = TwoHopRoute(channel.RemoteNodeId, destination);
        route.Hops[1].CustomRecords.Add(CustomRecordCodec.KeysendPreimageType, ByteString.CopyFrom(preimage));
        route.Hops[1].CustomRecords.Add(34_349_334, ByteString.CopyFromUtf8("hello"));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var attempt = await connection.RouterClient.SendToRouteV2Async(new SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom((byte[])hash),
            Route = route
        }, cancellationToken: Ct);

        // Assert: the preimage taken out of the records; an HTLC never offered is LND's local temporary failure
        Assert.Equal(preimage, (byte[])captured!.KeysendPreimage!.Value);
        var record = Assert.Single(captured.CustomRecords);
        Assert.Equal(34_349_334ul, record.Type);
        Assert.Equal(Failure.Types.FailureCode.TemporaryChannelFailure, attempt.Failure.Code);
        Assert.Equal(0u, attempt.Failure.FailureSourceIndex);
    }

    [Fact]
    public async Task Given_OurChannelCannotCarryIt_When_SendToRouteV2_Then_ATemporaryChannelFailureAtOurNode()
    {
        // Arrange
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        _paymentService.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                   It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new PayRouteLiquidityException("cannot carry", "routes"));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var attempt = await connection.RouterClient.SendToRouteV2Async(new SendToRouteRequest
        {
            PaymentHash = ByteString.CopyFrom(new byte[32]),
            Route = TwoHopRoute(channel.RemoteNodeId, RealKey())
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal(HTLCAttempt.Types.HTLCStatus.Failed, attempt.Status);
        Assert.Equal(Failure.Types.FailureCode.TemporaryChannelFailure, attempt.Failure.Code);
        Assert.Equal(0u, attempt.Failure.FailureSourceIndex);
    }

    /// <summary>Our channel 7's peer forwarding 10,000 msat to the destination for a 1,000 msat fee.</summary>
    private static Route TwoHopRoute(CompactPubKey peer, CompactPubKey destination)
    {
        var route = new Route { TotalAmtMsat = 11_000, TotalFeesMsat = 1_000, TotalTimeLock = 300 };
        route.Hops.Add(new Hop
        {
            ChanId = LightningService.ToChanId(new ShortChannelId(150, 7, 0)),
            PubKey = peer.ToString(),
            AmtToForwardMsat = 10_000,
            FeeMsat = 1_000,
            Expiry = 260
        });
        route.Hops.Add(new Hop
        {
            ChanId = LightningService.ToChanId(new ShortChannelId(160, 1, 1)),
            PubKey = destination.ToString(),
            AmtToForwardMsat = 10_000,
            Expiry = 260
        });
        return route;
    }
}