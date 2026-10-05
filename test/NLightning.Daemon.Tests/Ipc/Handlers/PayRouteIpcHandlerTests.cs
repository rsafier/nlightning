using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Onion.Enums;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The payroute IPC handler (ClientCommand 48, NL-1082) end to end over MessagePack, with the real client handler
/// and a mocked <see cref="IPaymentService"/>.
/// </summary>
public class PayRouteIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());
    private static readonly CompactPubKey s_peer = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly CompactPubKey s_payee = new([0x03, .. Enumerable.Repeat((byte)0x22, 32)]);
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x07, 32).ToArray());

    private readonly Mock<IPaymentService> _paymentServiceMock = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepositoryMock = new();

    public PayRouteIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_PayRouteRequestWithInvoice_When_HandleAsync_Then_RequestAndOutcomesCrossTheWire()
    {
        // Arrange
        var payment = CreatePayment();
        payment.AddOutgoingHtlc(new byte[32], 0);
        payment.Succeed(s_preimage, s_now.AddSeconds(1));
        PayRouteRequest? captured = null;
        PayInvoiceOptions? capturedOptions = null;
        _paymentServiceMock
           .Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                       It.IsAny<CancellationToken>()))
           .Callback((PayRouteRequest request, PayInvoiceOptions options, CancellationToken _) =>
           {
               captured = request;
               capturedOptions = options;
           })
           .ReturnsAsync(new PayRouteResult(payment,
           [
               new RouteOutcome(0, PaymentPartState.Succeeded, 5UL, null, null, null),
               new RouteOutcome(1, PaymentPartState.Failed, 6UL, FailureCode.TemporaryChannelFailure, 1,
                                "wtmp err")
           ]));
        var handler = new PayRouteIpcHandler(NullLogger<PayRouteIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayRoute, new PayRouteIpcRequest
        {
            Bolt11 = "lnbcrt1pay",
            TotalMsatMsat = 200_000,
            Routes =
            [
                new PayRouteRouteIpcInfo
                {
                    FirstHopChannel = Convert.ToHexString((byte[])s_channelId),
                    FirstHopAmountMsat = 101_000,
                    FirstHopCltv = 850,
                    Hops =
                    [
                        new PayRouteHopIpcInfo
                        {
                            NodeId = s_peer,
                            OutgoingShortChannelId = ListGraphChannelsIpcResponse.ToNumber(
                                new ShortChannelId(500, 1, 0)),
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
                    FirstHopChannel = Convert.ToHexString((byte[])s_channelId),
                    FirstHopAmountMsat = 99_000,
                    FirstHopCltv = 850,
                    Hops = [new PayRouteHopIpcInfo { NodeId = s_payee, AmountToForwardMsat = 99_000, OutgoingCltvValue = 830 }]
                }
            ],
            TimeoutSeconds = 45,
            MaxFeeMsat = 9_000,
            Label = "rebalance",
            Tags = ["purpose=test"]
        });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        Assert.NotNull(captured);
        Assert.Equal("lnbcrt1pay", captured.Bolt11);
        Assert.Null(captured.PaymentHash);
        Assert.Null(captured.PaymentSecret);
        Assert.Equal(200_000UL, captured.TotalAmount!.MilliSatoshi);
        Assert.Equal(2, captured.Routes.Count);
        Assert.Equal(s_channelId, captured.Routes[0].FirstHopChannelId);
        Assert.Equal(101_000UL, captured.Routes[0].FirstHopAmount.MilliSatoshi);
        Assert.Equal(850U, captured.Routes[0].FirstHopCltvExpiry);
        Assert.Equal(2, captured.Routes[0].Hops.Count);
        Assert.Equal(s_peer, captured.Routes[0].Hops[0].NodeId);
        Assert.Equal(new ShortChannelId(500, 1, 0), captured.Routes[0].Hops[0].OutgoingShortChannelId);
        Assert.Equal(100_500UL, captured.Routes[0].Hops[0].AmountToForward.MilliSatoshi);
        Assert.Equal(840U, captured.Routes[0].Hops[0].OutgoingCltvValue);
        Assert.Null(captured.Routes[0].Hops[1].OutgoingShortChannelId);
        Assert.Equal(100_000UL, captured.Routes[0].Hops[1].AmountToForward.MilliSatoshi);
        Assert.Equal(s_payee, captured.Routes[1].Hops[0].NodeId);
        Assert.NotNull(capturedOptions);
        Assert.Equal(TimeSpan.FromSeconds(45), capturedOptions.Timeout);
        Assert.Equal(9_000UL, capturedOptions.MaxFee!.MilliSatoshi);
        Assert.Equal("rebalance", capturedOptions.Labels.Label);
        Assert.Equal(["purpose=test"], capturedOptions.Labels.TagStrings);
        var payload = Deserialize<PayRouteIpcResponse>(response);
        Assert.Equal(PaymentStatus.Succeeded, payload.Payment.Status);
        Assert.Equal(s_preimage, payload.Payment.Preimage);
        Assert.Equal(2, payload.RouteOutcomes.Count);
        Assert.Equal((0, PaymentPartState.Succeeded, 5UL),
                     (payload.RouteOutcomes[0].Index, payload.RouteOutcomes[0].Status, payload.RouteOutcomes[0].HtlcId));
        Assert.Null(payload.RouteOutcomes[0].FailureCode);
        Assert.Null(payload.RouteOutcomes[0].FailureReason);
        Assert.Equal((1, FailureCode.TemporaryChannelFailure, 1, "wtmp err"),
                     (payload.RouteOutcomes[1].Index, payload.RouteOutcomes[1].FailureCode,
                      payload.RouteOutcomes[1].FailureSourceIndex, payload.RouteOutcomes[1].FailureReason));
        _paymentServiceMock.Verify(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                        It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_PayRouteRequestWithPaymentHash_When_HandleAsync_Then_TheRawFormCrossesTheWire()
    {
        // Arrange: the LND SendToRoute form — a raw hash, a secret and an explicit total
        PayRouteRequest? captured = null;
        _paymentServiceMock
           .Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                       It.IsAny<CancellationToken>()))
           .Callback((PayRouteRequest request, PayInvoiceOptions _, CancellationToken _) => captured = request)
           .ReturnsAsync(new PayRouteResult(CreatePayment(),
                                           [new RouteOutcome(0, PaymentPartState.InFlight, null, null, null, null)]));
        var handler = new PayRouteIpcHandler(NullLogger<PayRouteIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayRoute, new PayRouteIpcRequest
        {
            PaymentHash = s_paymentHash,
            PaymentSecret = s_preimage,
            TotalMsatMsat = 50_000,
            Routes = [CreateRoute(50_000)]
        });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        Assert.NotNull(captured);
        Assert.Null(captured.Bolt11);
        Assert.Equal(s_paymentHash, captured.PaymentHash);
        Assert.Equal(s_preimage, captured.PaymentSecret);
        Assert.Equal(50_000UL, captured.TotalAmount!.MilliSatoshi);
        var outcome = Assert.Single(Deserialize<PayRouteIpcResponse>(response).RouteOutcomes);
        Assert.Equal(PaymentPartState.InFlight, outcome.Status);
        Assert.Null(outcome.HtlcId);
    }

    [Fact]
    public async Task Given_FirstHopByShortChannelId_When_HandleAsync_Then_OurChannelIsResolved()
    {
        // Arrange - NL-609: the first hop names a channel of ours by its short channel id
        var channel = CreateChannel(0x0A, new ShortChannelId(500, 1, 0));
        _channelMemoryRepositoryMock.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                                    .Returns((Func<ChannelModel, bool> predicate) =>
                                                 new[] { channel }.Where(predicate).ToList());
        PayRouteRequest? captured = null;
        _paymentServiceMock
           .Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                       It.IsAny<CancellationToken>()))
           .Callback((PayRouteRequest request, PayInvoiceOptions _, CancellationToken _) => captured = request)
           .ReturnsAsync(new PayRouteResult(CreatePayment(), [new RouteOutcome(0, PaymentPartState.Succeeded, 0UL, null, null, null)]));
        var handler = new PayRouteIpcHandler(NullLogger<PayRouteIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayRoute, new PayRouteIpcRequest
        {
            Bolt11 = "lnbcrt1pay",
            Routes = [CreateRoute(50_000, "500x1x0")]
        });

        // Act
        await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(captured);
        Assert.Equal(channel.ChannelId, captured.Routes[0].FirstHopChannelId);
    }

    [Fact]
    public async Task Given_BothIdentities_When_HandleAsync_Then_InvalidOperationIsReturned()
    {
        // Arrange
        var handler = new PayRouteIpcHandler(NullLogger<PayRouteIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayRoute, new PayRouteIpcRequest
        {
            Bolt11 = "lnbcrt1pay",
            PaymentHash = s_paymentHash,
            Routes = [CreateRoute(50_000)]
        });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("exactly one", error.Message, StringComparison.OrdinalIgnoreCase);
        _paymentServiceMock.Verify(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                        It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_NoRoutes_When_HandleAsync_Then_InvalidOperationIsReturned()
    {
        // Arrange
        var handler = new PayRouteIpcHandler(NullLogger<PayRouteIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayRoute,
                                      new PayRouteIpcRequest { Bolt11 = "lnbcrt1pay", Routes = [] });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("route", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0U)]
    [InlineData(301U)]
    public async Task Given_TimeoutOutOfRange_When_HandleAsync_Then_InvalidOperationIsReturned(uint timeoutSeconds)
    {
        // Arrange
        var handler = new PayRouteIpcHandler(NullLogger<PayRouteIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayRoute, new PayRouteIpcRequest
        {
            Bolt11 = "lnbcrt1pay",
            Routes = [CreateRoute(50_000)],
            TimeoutSeconds = timeoutSeconds
        });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Given_PaymentAlreadyInFlight_When_HandleAsync_Then_InvalidOperationIsReturned()
    {
        // Arrange
        _paymentServiceMock.Setup(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                                       It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new InvalidOperationException("already in flight"));
        var handler = new PayRouteIpcHandler(NullLogger<PayRouteIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayRoute,
                                      new PayRouteIpcRequest { Bolt11 = "lnbcrt1pay", Routes = [CreateRoute(50_000)] });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal("already in flight", error.Message);
    }

    [Fact]
    public void Given_PayRouteHandler_When_CommandRead_Then_ItServesClientCommandPayRoute48()
    {
        // Arrange
        var handler = new PayRouteIpcHandler(NullLogger<PayRouteIpcHandler>.Instance, BuildProvider());

        // Act / Assert
        Assert.Equal(ClientCommand.PayRoute, handler.Command);
        Assert.Equal(48, (int)handler.Command);
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_paymentServiceMock.Object);
        services.AddSingleton(_channelMemoryRepositoryMock.Object);
        services.AddScoped<IClientCommandHandler<PayRouteClientRequest, PayRouteClientResponse>,
            PayRouteClientHandler>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static PayRouteRouteIpcInfo CreateRoute(ulong amountMsat, string? firstHopChannel = null) => new()
    {
        FirstHopChannel = firstHopChannel ?? Convert.ToHexString((byte[])s_channelId),
        FirstHopAmountMsat = amountMsat,
        FirstHopCltv = 850,
        Hops = [new PayRouteHopIpcInfo { NodeId = s_payee, AmountToForwardMsat = amountMsat, OutgoingCltvValue = 830 }]
    };

    private static PaymentModel CreatePayment() =>
        new(s_paymentHash, "lnbcrt1pay", s_payee, LightningMoney.MilliSatoshis(10_000), LightningMoney.Zero, s_now);

    /// <summary>An open channel with <paramref name="shortChannelId"/> (what the first-hop resolution reads).</summary>
    private static ChannelModel CreateChannel(byte tag, ShortChannelId shortChannelId)
    {
        var peerId = new CompactPubKey([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
        return new ChannelModel(new ChannelParams(), new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), null, null,
                                true, null, null, LightningMoney.Satoshis(100_000),
                                new ChannelKeySetModel(0, peerId, peerId, peerId, peerId, peerId, peerId), 0, 0,
                                LightningMoney.Zero, null, 0, peerId, 0, ChannelState.Open, ChannelVersion.V1)
        {
            ShortChannelId = shortChannelId
        };
    }

    private static IpcEnvelope CreateEnvelope<T>(ClientCommand command, T request) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };

    private static void AssertResponseEnvelope(IpcEnvelope request, IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal(request.Command, response.Command);
        Assert.Equal(request.CorrelationId, response.CorrelationId);
        Assert.Equal(request.Version, response.Version);
    }

    private static IpcError AssertError(IpcEnvelope request, IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        Assert.Equal(request.CorrelationId, response.CorrelationId);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private static T Deserialize<T>(IpcEnvelope response) =>
        MessagePackSerializer.Deserialize<T>(response.Payload, s_options, TestContext.Current.CancellationToken);
}