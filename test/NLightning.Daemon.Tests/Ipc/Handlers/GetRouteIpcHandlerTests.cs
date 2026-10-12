using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Application.Payments.Routing;
using Application.Payments.Routing.Interfaces;
using Application.Payments.Send;
using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// BOLT 7 G4-T4: getroute (ClientCommand 19) over IPC, from the payment service's route quote through the daemon's
/// client handler and the MessagePack contract: every hop's channel, amount, CLTV and fee.
/// </summary>
public class GetRouteIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly CompactPubKey s_carol = PubKey(0x03, 3);
    private static readonly CompactPubKey s_david = PubKey(0x02, 4);
    private static readonly CompactPubKey s_erin = PubKey(0x02, 5);
    private static readonly ShortChannelId s_ourScid = new(300, 1, 0);
    private static readonly ShortChannelId s_scidCd = new(101, 2, 1);
    private static readonly ShortChannelId s_scidDe = new(102, 3, 0);
    private static readonly ChannelId s_ourChannel = new(Enumerable.Repeat((byte)0xC1, 32).ToArray());

    private readonly Mock<IRouteQueryService> _routes = new();

    public GetRouteIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    /// <summary>
    /// Us → Carol → David → Erin for 1,000,000 msat: Carol keeps 2,500 msat, David 1,100 msat; 30 and 40 blocks of
    /// CLTV delta above Erin's 743.
    /// </summary>
    private static RouteQuote Quote()
    {
        var route = new PaymentRoute([
                                         new RouteHop(s_carol, LightningMoney.MilliSatoshis(1_001_100), 773, s_scidCd),
                                         new RouteHop(s_david, LightningMoney.MilliSatoshis(1_000_000), 743, s_scidDe),
                                         new RouteHop(s_erin, LightningMoney.MilliSatoshis(1_000_000), 743, null)
                                     ], LightningMoney.MilliSatoshis(1_003_600), 813, new Hash(new byte[32]),
                                     new Secret(new byte[32]));
        return new RouteQuote(route, new LocalChannelCandidate(s_ourChannel, s_carol, s_ourScid), 0.36, 700,
                              "graph route over 300x1x0");
    }

    /// <summary>
    /// Our one-hop outer route to Carol, the trampoline node, for 1,002,000 msat at 1,319 (Erin's 743 plus the default
    /// policy's delta 576), with the layer a payment through her would carry (NL-940).
    /// </summary>
    private static RouteQuote QuoteThroughTrampoline()
    {
        var route = new PaymentRoute([new RouteHop(s_carol, LightningMoney.MilliSatoshis(1_002_000), 1_319, null)],
                                     LightningMoney.MilliSatoshis(1_002_000), 1_319, new Hash(new byte[32]),
                                     new Secret(new byte[32]));
        return new RouteQuote(route, new LocalChannelCandidate(s_ourChannel, s_carol, s_ourScid), 1.0, 700,
                              "direct channel 300x1x0",
                              new TrampolineQuote(s_carol, s_erin, LightningMoney.MilliSatoshis(1_000_000), 743,
                                                  new TrampolinePolicy(1_000, 1_000, 576), PolicyLearnt: false,
                                                  LightningMoney.MilliSatoshis(2_000)));
    }

    [Fact]
    public async Task Given_ARouteQuote_When_GetRoute_Then_EveryHopCrossesTheWireWithItsFeeAndCltv()
    {
        // Arrange
        _routes.Setup(r => r.QuoteRouteAsync(s_erin, LightningMoney.MilliSatoshis(1_000_000),
                                             LightningMoney.MilliSatoshis(5_000), (ushort?)40,
                                             It.IsAny<CompactPubKey?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Quote());
        using var provider = BuildProvider();
        var handler = new GetRouteIpcHandler(NullLogger<GetRouteIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(new GetRouteIpcRequest
        {
            NodeId = s_erin,
            AmountMsat = 1_000_000,
            MaxFeeMsat = 5_000,
            FinalCltvDelta = 40
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal(ClientCommand.GetRoute, response.Command);
        var payload = MessagePackSerializer.Deserialize<GetRouteIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
        Assert.Equal(s_ourChannel, payload.ChannelId);
        Assert.Equal((1_003_600UL, 3_600UL, 813U, 700U, 0.36, "graph route over 300x1x0"),
                     (payload.AmountMsat, payload.FeeMsat, payload.CltvExpiry, payload.BlockHeight,
                      payload.Probability, payload.Description));
        Assert.Equal(3, payload.Hops.Count);
        Assert.Equal((s_carol, ToNumber(s_ourScid), 1_003_600UL, 813U, 2_500UL), Tuple(payload.Hops[0]));
        Assert.Equal((s_david, ToNumber(s_scidCd), 1_001_100UL, 773U, 1_100UL), Tuple(payload.Hops[1]));
        Assert.Equal((s_erin, ToNumber(s_scidDe), 1_000_000UL, 743U, 0UL), Tuple(payload.Hops[2]));
    }

    [Fact]
    public async Task Given_NoRoute_When_GetRoute_Then_InvalidOperationWithThePlannersReason()
    {
        // Arrange
        _routes.Setup(r => r.QuoteRouteAsync(It.IsAny<CompactPubKey>(), It.IsAny<LightningMoney>(),
                                             It.IsAny<LightningMoney?>(), It.IsAny<ushort?>(),
                                             It.IsAny<CompactPubKey?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("No route to the payee: the graph has no path."));
        using var provider = BuildProvider();
        var handler = new GetRouteIpcHandler(NullLogger<GetRouteIpcHandler>.Instance, provider);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var response = await handler.HandleAsync(Envelope(new GetRouteIpcRequest
        {
            NodeId = s_erin,
            AmountMsat = 1_000
        }), ct);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options, ct);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("graph has no path", error.Message);
    }

    [Fact]
    public async Task Given_ATrampolineQuote_When_GetRoute_Then_TheOuterRouteAndItsPolicyCrossTheWire()
    {
        // Arrange: our one-hop outer route to Carol, the trampoline node, around the default policy (1,000 msat +
        // 1,000 ppm, delta 576): a fee of 2,000 msat on Erin's 1,000,000 msat (NL-940)
        _routes.Setup(r => r.QuoteRouteAsync(s_erin, LightningMoney.MilliSatoshis(1_000_000), null, null, s_carol,
                                             It.IsAny<CancellationToken>()))
               .ReturnsAsync(QuoteThroughTrampoline());
        using var provider = BuildProvider();
        var handler = new GetRouteIpcHandler(NullLogger<GetRouteIpcHandler>.Instance, provider);

        // Act
        var response = await handler.HandleAsync(Envelope(new GetRouteIpcRequest
        {
            NodeId = s_erin,
            AmountMsat = 1_000_000,
            TrampolineNode = s_carol
        }), TestContext.Current.CancellationToken);

        // Assert: the outer hop carries the trampoline node's fee, and the layer names the payee and the policy
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<GetRouteIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
        Assert.Equal((1_002_000UL, 2_000UL, 1_319U), (payload.AmountMsat, payload.FeeMsat, payload.CltvExpiry));
        var hop = Assert.Single(payload.Hops);
        Assert.Equal((s_carol, ToNumber(s_ourScid), 1_002_000UL, 1_319U, 2_000UL), Tuple(hop));
        var trampoline = payload.Trampoline;
        Assert.NotNull(trampoline);
        Assert.Equal((s_carol, s_erin, 1_000_000UL, 743U, 1_000U, 1_000U, (ushort)576, 2_000UL, false),
                     (trampoline.TrampolineNode, trampoline.Payee, trampoline.AmountMsat,
                      trampoline.PayeeCltvExpiry, trampoline.FeeBaseMsat, trampoline.FeeProportionalMillionths,
                      trampoline.CltvExpiryDelta, trampoline.FeeMsat, trampoline.PolicyLearnt));
    }

    [Fact]
    public async Task Given_AZeroAmount_When_GetRoute_Then_InvalidOperationWithoutAQuote()
    {
        // Arrange
        using var provider = BuildProvider();
        var handler = new GetRouteIpcHandler(NullLogger<GetRouteIpcHandler>.Instance, provider);
        var ct = TestContext.Current.CancellationToken;

        // Act
        var response = await handler.HandleAsync(Envelope(new GetRouteIpcRequest { NodeId = s_erin }), ct);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        Assert.Equal(ErrorCodes.InvalidOperation,
                     MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options, ct).Code);
        _routes.VerifyNoOtherCalls();
    }

    [Fact]
    public void Given_AGetRouteRequest_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var request = new GetRouteIpcRequest
        {
            NodeId = s_david,
            AmountMsat = 42,
            MaxFeeMsat = 7,
            FinalCltvDelta = 9,
            TrampolineNode = s_carol
        };

        // Act
        var clientRequest = MessagePackSerializer.Deserialize<GetRouteIpcRequest>(
            MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken), s_options,
            TestContext.Current.CancellationToken).ToClientRequest();
        var defaults = new GetRouteIpcRequest { NodeId = s_david, AmountMsat = 42 }.ToClientRequest();

        // Assert
        Assert.Equal((s_david, 42UL, 7UL, (ushort?)9, s_carol),
                     (clientRequest.NodeId, clientRequest.Amount.MilliSatoshi, clientRequest.MaxFee!.MilliSatoshi,
                      clientRequest.FinalCltvDelta, clientRequest.TrampolineNode));
        Assert.Null(defaults.MaxFee);
        Assert.Null(defaults.FinalCltvDelta);
        Assert.Null(defaults.TrampolineNode);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddScoped<IClientCommandHandler<GetRouteClientRequest, GetRouteClientResponse>>(_ =>
            new GetRouteClientHandler(_routes.Object));
        return services.BuildServiceProvider();
    }

    private static (CompactPubKey, ulong, ulong, uint, ulong) Tuple(GetRouteHopIpcInfo hop) =>
        (hop.NodeId, hop.ShortChannelId, hop.AmountMsat, hop.CltvExpiry, hop.FeeMsat);

    private static IpcEnvelope Envelope(GetRouteIpcRequest request) =>
        new()
        {
            Version = 1,
            Command = ClientCommand.GetRoute,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };

    private static ulong ToNumber(ShortChannelId shortChannelId) =>
        ListGraphChannelsIpcResponse.ToNumber(shortChannelId);

    private static CompactPubKey PubKey(byte prefix, byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = prefix;
        return new CompactPubKey(bytes);
    }
}