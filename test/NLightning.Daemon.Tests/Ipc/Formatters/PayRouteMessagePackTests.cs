using MessagePack;

namespace NLightning.Daemon.Tests.Ipc.Formatters;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// MessagePack round trips of the payroute IPC contract (ClientCommand 48, NL-1145), both identity forms.
/// </summary>
public class PayRouteMessagePackTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;

    private static readonly Hash s_paymentHash = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly Secret s_paymentSecret = new(Enumerable.Range(33, 32).Select(i => (byte)i).ToArray());
    private static readonly Secret s_preimage = new(Enumerable.Range(65, 32).Select(i => (byte)i).ToArray());
    private static readonly CompactPubKey s_peer = new([0x02, .. Enumerable.Repeat((byte)4, 32)]);
    private static readonly CompactPubKey s_payee = new([0x03, .. Enumerable.Repeat((byte)5, 32)]);
    private static readonly DateTimeOffset s_createdAt = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    [Fact]
    public void Given_PayRouteRequestWithInvoice_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange: two routes (an MPP shard set), the first with an intermediate hop before the payee
        var request = new PayRouteIpcRequest
        {
            Bolt11 = "lnbcrt1pay",
            TotalMsatMsat = 200_000,
            Routes =
            [
                new PayRouteRouteIpcInfo
                {
                    FirstHopChannel = "500x1x0",
                    FirstHopAmountMsat = 101_000,
                    FirstHopCltv = 850,
                    Hops =
                    [
                        new PayRouteHopIpcInfo
                        {
                            NodeId = s_peer,
                            OutgoingShortChannelId = ListGraphChannelsIpcResponse.ToNumber(new ShortChannelId(500, 2, 1)),
                            AmountToForwardMsat = 100_500,
                            OutgoingCltvValue = 840
                        },
                        new PayRouteHopIpcInfo
                        {
                            NodeId = s_payee, AmountToForwardMsat = 100_000, OutgoingCltvValue = 830
                        }
                    ]
                },
                new PayRouteRouteIpcInfo
                {
                    FirstHopChannel = new string('a', 64),
                    FirstHopAmountMsat = 99_000,
                    FirstHopCltv = 851,
                    Hops = [new PayRouteHopIpcInfo { NodeId = s_payee, AmountToForwardMsat = 99_000, OutgoingCltvValue = 831 }]
                }
            ],
            TimeoutSeconds = 45,
            MaxFeeMsat = 9_000,
            Label = "rebalance",
            Tags = ["a=b", "c=d"]
        };

        // Act
        var result = RoundTrip(request);
        var clientRequest = result.ToClientRequest();

        // Assert
        Assert.Equal("lnbcrt1pay", clientRequest.Bolt11);
        Assert.Null(clientRequest.PaymentHash);
        Assert.Equal(200_000UL, clientRequest.TotalMsatMsat);
        Assert.Equal(45U, clientRequest.TimeoutSeconds);
        Assert.Equal(9_000UL, clientRequest.MaxFeeMsat);
        Assert.Equal("rebalance", clientRequest.Label);
        Assert.Equal(["a=b", "c=d"], clientRequest.Tags);
        Assert.Equal(2, clientRequest.Routes.Count);
        var route = clientRequest.Routes[0];
        Assert.Equal("500x1x0", route.FirstHopChannel);
        Assert.Equal((101_000UL, 850U), (route.FirstHopAmountMsat, route.FirstHopCltv));
        Assert.Equal(2, route.Hops.Count);
        Assert.Equal(s_peer, route.Hops[0].NodeId);
        Assert.Equal(ListGraphChannelsIpcResponse.ToNumber(new ShortChannelId(500, 2, 1)),
                     route.Hops[0].OutgoingShortChannelId);
        Assert.Equal((100_500UL, 840U), (route.Hops[0].AmountToForwardMsat, route.Hops[0].OutgoingCltvValue));
        Assert.Equal(s_payee, route.Hops[1].NodeId);
        Assert.Null(route.Hops[1].OutgoingShortChannelId);
        Assert.Equal((100_000UL, 830U), (route.Hops[1].AmountToForwardMsat, route.Hops[1].OutgoingCltvValue));
        Assert.Equal(64, clientRequest.Routes[1].FirstHopChannel.Length);
        Assert.Equal((99_000UL, 851U),
                     (clientRequest.Routes[1].FirstHopAmountMsat, clientRequest.Routes[1].FirstHopCltv));
        Assert.Equal(s_payee, clientRequest.Routes[1].Hops[0].NodeId);
    }

    [Fact]
    public void Given_PayRouteRequestWithPaymentHash_When_RoundTripped_Then_TheRawFormIsPreserved()
    {
        // Arrange: the LND SendToRoute form — a raw hash, a secret and an explicit total
        var request = new PayRouteIpcRequest
        {
            PaymentHash = s_paymentHash,
            PaymentSecret = s_paymentSecret,
            TotalMsatMsat = 50_000,
            Routes = [CreateRoute(50_000)]
        };

        // Act
        var clientRequest = RoundTrip(request).ToClientRequest();

        // Assert
        Assert.Null(clientRequest.Bolt11);
        Assert.Equal(s_paymentHash, clientRequest.PaymentHash);
        Assert.Equal(s_paymentSecret, clientRequest.PaymentSecret);
        Assert.Equal(50_000UL, clientRequest.TotalMsatMsat);
    }

    [Fact]
    public void Given_MinimalPayRouteRequest_When_RoundTripped_Then_NewKeysDefaultToNullsAndTheDefaultTimeout()
    {
        // Arrange: an older client sends only the identity and the routes
        var request = new PayRouteIpcRequest { Bolt11 = "lnbcrt1pay", Routes = [CreateRoute(50_000)] };

        // Act
        var result = RoundTrip(request);
        var clientRequest = result.ToClientRequest();

        // Assert
        Assert.Null(result.PaymentHash);
        Assert.Null(result.PaymentSecret);
        Assert.Null(result.TotalMsatMsat);
        Assert.Null(result.MaxFeeMsat);
        Assert.Null(result.Label);
        Assert.Null(result.Tags);
        Assert.Null(result.TimeoutSeconds);
        Assert.Null(clientRequest.Tags);
        Assert.Equal(60U, clientRequest.TimeoutSeconds);
    }

    [Fact]
    public void Given_PayRouteResponse_When_RoundTripped_Then_PaymentAndOutcomesArePreserved()
    {
        // Arrange
        var response = new PayRouteIpcResponse
        {
            Payment = CreateSucceededPayment(),
            RouteOutcomes =
            [
                new RouteOutcomeIpcInfo { Index = 0, Status = PaymentPartState.Succeeded, HtlcId = 5 },
                new RouteOutcomeIpcInfo
                {
                    Index = 1,
                    Status = PaymentPartState.Failed,
                    HtlcId = 6,
                    FailureCode = (FailureCode)0x4099,
                    FailureSourceIndex = 2,
                    FailureReason = "unknown code"
                },
                new RouteOutcomeIpcInfo { Index = 2, Status = PaymentPartState.InFlight }
            ]
        };

        // Act
        var result = RoundTrip(response);

        // Assert
        Assert.Equal(PaymentStatus.Succeeded, result.Payment.Status);
        Assert.Equal(s_preimage, result.Payment.Preimage);
        Assert.Equal(3, result.RouteOutcomes.Count);
        Assert.Equal((0, PaymentPartState.Succeeded, 5UL),
                     (result.RouteOutcomes[0].Index, result.RouteOutcomes[0].Status, result.RouteOutcomes[0].HtlcId));
        Assert.Null(result.RouteOutcomes[0].FailureCode);
        Assert.Equal((FailureCode)0x4099, result.RouteOutcomes[1].FailureCode);
        Assert.Equal((1, 2, "unknown code"),
                     (result.RouteOutcomes[1].Index, result.RouteOutcomes[1].FailureSourceIndex,
                      result.RouteOutcomes[1].FailureReason));
        Assert.Equal(PaymentPartState.InFlight, result.RouteOutcomes[2].Status);
        Assert.Null(result.RouteOutcomes[2].HtlcId);
        Assert.Null(result.RouteOutcomes[2].FailureCode);
        Assert.Null(result.RouteOutcomes[2].FailureSourceIndex);
        Assert.Null(result.RouteOutcomes[2].FailureReason);
    }

    private static PayRouteRouteIpcInfo CreateRoute(ulong amountMsat) => new()
    {
        FirstHopChannel = "500x1x0",
        FirstHopAmountMsat = amountMsat,
        FirstHopCltv = 850,
        Hops = [new PayRouteHopIpcInfo { NodeId = s_payee, AmountToForwardMsat = amountMsat, OutgoingCltvValue = 830 }]
    };

    private static PaymentInfoIpcResponse CreateSucceededPayment() => new()
    {
        PaymentHash = s_paymentHash,
        Bolt11 = "lnbcrt1pay",
        PayeeNodeId = s_payee,
        Amount = LightningMoney.MilliSatoshis(50_000),
        Fee = LightningMoney.MilliSatoshis(1_000),
        Status = PaymentStatus.Succeeded,
        Preimage = s_preimage,
        CreatedAt = s_createdAt,
        CompletedAt = s_createdAt.AddSeconds(3)
    };

    private static T RoundTrip<T>(T value)
    {
        var bytes = MessagePackSerializer.Serialize(value, s_options, TestContext.Current.CancellationToken);
        return MessagePackSerializer.Deserialize<T>(bytes, s_options, TestContext.Current.CancellationToken);
    }
}