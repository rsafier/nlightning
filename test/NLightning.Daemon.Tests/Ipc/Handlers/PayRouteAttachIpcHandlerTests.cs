using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Daemon.Services.Ipc;
using Domain.Channels.Interfaces;
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
using NLightning.Client;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>payroute --attach</c> (ClientCommand 56, NL-1276) end to end over MessagePack, with the real client handlers and
/// a mocked <see cref="IPaymentService"/>; its wire layout and the CLI flag.
/// </summary>
public class PayRouteAttachIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Secret s_secret = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());
    private static readonly CompactPubKey s_payee = new([0x03, .. Enumerable.Repeat((byte)0x22, 32)]);
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x07, 32).ToArray());

    private readonly Mock<IPaymentService> _paymentServiceMock = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemoryRepositoryMock = new();

    public PayRouteAttachIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_AnAttachRequest_When_HandleAsync_Then_ItAttachesAndOnlyItsRoutesComeBack()
    {
        // Arrange
        var payment = new PaymentModel(s_paymentHash, null, s_payee, LightningMoney.MilliSatoshis(60_000),
                                       LightningMoney.Zero, s_now);
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
                                                new RouteOutcome(0, PaymentPartState.Failed, 9UL,
                                                                 FailureCode.TemporaryChannelFailure, 0, "peer")
                                            ]));
        var handler = new PayRouteAttachIpcHandler(NullLogger<PayRouteAttachIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(new PayRouteAttachIpcRequest
        {
            PaymentHash = s_paymentHash,
            PaymentSecret = s_secret,
            TotalMsatMsat = 60_000,
            Routes = [CreateRoute(25_000)],
            TimeoutSeconds = 30,
            MaxFeeMsat = 4_000
        });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert: the payment service was asked to attach, with the identity, the route and the limits
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal(ClientCommand.PayRouteAttach, response.Command);
        Assert.NotNull(captured);
        Assert.Equal(PayRouteAttachMode.Required, captured.Attach);
        Assert.False(captured.IndependentShards);
        Assert.Equal(s_paymentHash, captured.PaymentHash);
        Assert.Equal(s_secret, captured.PaymentSecret);
        Assert.Equal(60_000UL, captured.TotalAmount!.MilliSatoshi);
        var route = Assert.Single(captured.Routes);
        Assert.Equal(s_channelId, route.FirstHopChannelId);
        Assert.Equal(25_000UL, route.FirstHopAmount.MilliSatoshi);
        Assert.Equal(TimeSpan.FromSeconds(30), capturedOptions!.Timeout);
        Assert.Equal(4_000UL, capturedOptions.MaxFee!.MilliSatoshi);
        Assert.Null(capturedOptions.Labels.Label);

        // Assert: the attached route's attributed failure crosses back
        var payload = MessagePackSerializer.Deserialize<PayRouteIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
        Assert.Equal(PaymentStatus.InFlight, payload.Payment.Status);
        var outcome = Assert.Single(payload.RouteOutcomes);
        Assert.Equal((PaymentPartState.Failed, FailureCode.TemporaryChannelFailure, 0),
                     (outcome.Status, outcome.FailureCode, outcome.FailureSourceIndex));
    }

    [Fact]
    public async Task Given_AnAttachThePaymentServiceRefuses_When_HandleAsync_Then_InvalidOperationWithItsReason()
    {
        // Arrange: nothing in flight (InvalidOperationException) and a mismatched total (ArgumentException)
        var handler = new PayRouteAttachIpcHandler(NullLogger<PayRouteAttachIpcHandler>.Instance, BuildProvider());
        var request = new PayRouteAttachIpcRequest { Bolt11 = "lnbcrt1pay", Routes = [CreateRoute(25_000)] };
        _paymentServiceMock
           .SetupSequence(x => x.PayRouteAsync(It.IsAny<PayRouteRequest>(), It.IsAny<PayInvoiceOptions>(),
                                               It.IsAny<CancellationToken>()))
           .ThrowsAsync(new InvalidOperationException("No payroute payment for payment hash ab is in flight"))
           .ThrowsAsync(new ArgumentException("The total 1 msat differs from the 60000 msat", "call"));

        // Act
        var nothing = await handler.HandleAsync(CreateEnvelope(request), TestContext.Current.CancellationToken);
        var mismatch = await handler.HandleAsync(CreateEnvelope(request), TestContext.Current.CancellationToken);

        // Assert
        var nothingError = Error(nothing);
        Assert.Equal(ErrorCodes.InvalidOperation, nothingError.Code);
        Assert.Contains("No payroute payment", nothingError.Message);
        var mismatchError = Error(mismatch);
        Assert.Equal(ErrorCodes.InvalidOperation, mismatchError.Code);
        Assert.Contains("differs from the 60000 msat", mismatchError.Message);
    }

    [Fact]
    public void Given_TheAttachCommand_When_Read_Then_ItIs56AndRefusedWhileDraining()
    {
        // Arrange
        var handler = new PayRouteAttachIpcHandler(NullLogger<PayRouteAttachIpcHandler>.Instance, BuildProvider());

        // Act / Assert: 52-55 stay reserved for the silent payments commands
        Assert.Equal(ClientCommand.PayRouteAttach, handler.Command);
        Assert.Equal(56, (int)ClientCommand.PayRouteAttach);
        Assert.Contains(ClientCommand.PayRouteAttach, IpcRequestRouter.RefusedWhileDraining);
    }

    [Fact]
    public void Given_AnAttachRequest_When_RoundTripped_Then_EveryKeyIsKeptAndAbsentOnesAreNull()
    {
        // Arrange
        var full = new PayRouteAttachIpcRequest
        {
            PaymentHash = s_paymentHash,
            PaymentSecret = s_secret,
            TotalMsatMsat = 60_000,
            Routes = [CreateRoute(25_000)],
            TimeoutSeconds = 30,
            MaxFeeMsat = 4_000
        };
        var minimal = new PayRouteAttachIpcRequest { Bolt11 = "lnbcrt1pay", Routes = [CreateRoute(25_000)] };

        // Act
        var fullBack = MessagePackSerializer.Deserialize<PayRouteAttachIpcRequest>(
            MessagePackSerializer.Serialize(full, s_options, TestContext.Current.CancellationToken), s_options,
            TestContext.Current.CancellationToken).ToClientRequest();
        var minimalBack = MessagePackSerializer.Deserialize<PayRouteAttachIpcRequest>(
            MessagePackSerializer.Serialize(minimal, s_options, TestContext.Current.CancellationToken), s_options,
            TestContext.Current.CancellationToken).ToClientRequest();

        // Assert
        Assert.Equal((s_paymentHash, s_secret, 60_000UL, 30U, 4_000UL),
                     (fullBack.PaymentHash!.Value, fullBack.PaymentSecret!.Value, fullBack.TotalMsatMsat!.Value,
                      fullBack.TimeoutSeconds, fullBack.MaxFeeMsat!.Value));
        Assert.Equal(25_000UL, Assert.Single(fullBack.Routes).FirstHopAmountMsat);
        Assert.Equal("lnbcrt1pay", minimalBack.Bolt11);
        Assert.Null(minimalBack.PaymentHash);
        Assert.Null(minimalBack.PaymentSecret);
        Assert.Null(minimalBack.TotalMsatMsat);
        Assert.Null(minimalBack.MaxFeeMsat);
        Assert.Equal(60U, minimalBack.TimeoutSeconds);
    }

    [Fact]
    public void Given_TheAttachFlag_When_PayRouteIsParsed_Then_ItTakesNoValueAndIsKept()
    {
        // Arrange: the invoice form with the flag last, the raw form with it first
        string[][] cases =
        [
            ["lnbcrt1", "--routes", "r.json", "--attach"],
            ["--attach", "--payment-hash", new string('a', 64), "--total-msat", "60000", "--routes=-"]
        ];

        foreach (var args in cases)
        {
            // Act
            var parsed = ClientApp.ParsePayRouteOptions(args, out var error);

            // Assert
            Assert.Null(error);
            Assert.NotNull(parsed);
            Assert.True(parsed.Attach);
        }

        Assert.Contains("[--attach]", ClientApp.PayRouteUsage);
        Assert.False(ClientApp.ParsePayRouteOptions(["lnbcrt1", "--routes", "r.json"], out _)!.Attach);
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_paymentServiceMock.Object);
        services.AddSingleton(_channelMemoryRepositoryMock.Object);
        services.AddScoped<PayRouteClientHandler>();
        services.AddScoped<IClientCommandHandler<PayRouteAttachClientRequest, PayRouteClientResponse>,
            PayRouteAttachClientHandler>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static PayRouteRouteIpcInfo CreateRoute(ulong amountMsat) => new()
    {
        FirstHopChannel = Convert.ToHexString((byte[])s_channelId),
        FirstHopAmountMsat = amountMsat,
        FirstHopCltv = 850,
        Hops = [new PayRouteHopIpcInfo { NodeId = s_payee, AmountToForwardMsat = amountMsat, OutgoingCltvValue = 830 }]
    };

    private static IpcEnvelope CreateEnvelope(PayRouteAttachIpcRequest request) => new()
    {
        Version = 1,
        Command = ClientCommand.PayRouteAttach,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };

    private static IpcError Error(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }
}