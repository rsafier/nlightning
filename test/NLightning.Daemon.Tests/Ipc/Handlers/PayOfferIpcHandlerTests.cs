using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>payoffer</c> and <c>fetchinvoice</c> (ClientCommand 29 and 30) over IPC: the request reaches
/// <see cref="IOfferPaymentService"/> with the options, the fetch and payment come back, and refusals carry the error
/// code the CLI shows.
/// </summary>
public class PayOfferIpcHandlerTests
{
    private const string Offer = "lno1qgsqvgnwgcg35z6ee2h3yczraddm72xrfua9uve2rlrm9deu7xyfzrc";

    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly Hash s_hash = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private static readonly CompactPubKey s_node =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));

    private readonly Mock<IOfferPaymentService> _service = new();
    private PayOfferRequest? _request;
    private PayOfferOptions? _options;

    public PayOfferIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        var fetched = new FetchedBolt12Invoice(new byte[] { 1, 2 }, new byte[] { 3 }, s_node,
                                               LightningMoney.MilliSatoshis(10_000), s_hash,
                                               DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), 7200, 2);
        _service.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                            It.IsAny<CancellationToken>()))
                .Callback<PayOfferRequest, PayOfferOptions, CancellationToken>((r, o, _) => (_request, _options) = (r, o))
                .ReturnsAsync(new PayOfferResult(new FetchInvoiceResult(FetchInvoiceStatus.Received, fetched, 1),
                                                 new PayInvoiceResult(Payment(), 2, 1)));
        _service.Setup(s => s.FetchInvoiceAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                It.IsAny<CancellationToken>()))
                .Callback<PayOfferRequest, PayOfferOptions, CancellationToken>((r, o, _) => (_request, _options) = (r, o))
                .ReturnsAsync(new FetchInvoiceResult(FetchInvoiceStatus.Received, fetched, 1));
    }

    [Fact]
    public async Task Given_APayOfferRequest_When_Handled_Then_TheServiceGetsItAndTheFetchAndPaymentComeBack()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.PayOffer);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.PayOffer, new PayOfferIpcRequest
        {
            Offer = Offer,
            Amount = LightningMoney.MilliSatoshis(12_000),
            Quantity = 2,
            PayerNote = "note",
            TimeoutSeconds = 45,
            MaxFee = LightningMoney.MilliSatoshis(3_000),
            MaxParts = 4
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<PayOfferIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
        Assert.Equal(FetchInvoiceStatus.Received, payload.Fetch.Status);
        Assert.Equal(s_node, payload.Fetch.NodeId);
        Assert.Equal(s_hash, payload.Fetch.PaymentHash);
        Assert.Equal(2, payload.Fetch.PathCount);
        Assert.Equal(new byte[] { 1, 2 }, payload.Fetch.Invoice);
        Assert.Equal(PaymentStatus.Succeeded, payload.Payment!.Status);
        Assert.Equal(2, payload.Attempts);
        Assert.Equal(Offer, _request!.Offer);
        Assert.Equal(12_000UL, _request.Amount!.MilliSatoshi);
        Assert.Equal(2UL, _request.Quantity);
        Assert.Equal("note", _request.PayerNote);
        Assert.Equal(TimeSpan.FromSeconds(45), _options!.Payment.Timeout);
        Assert.Equal(3_000UL, _options.Payment.MaxFee!.MilliSatoshi);
        Assert.Equal(4, _options.Payment.MaxParts);
    }

    [Fact]
    public async Task Given_AFetchInvoiceRequest_When_Handled_Then_OnlyTheInvoiceComesBack()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.FetchInvoice);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.FetchInvoice,
                                                                new PayOfferIpcRequest { Offer = Offer }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<FetchInvoiceIpcResponse>(response.Payload, s_options,
                                                                                 TestContext.Current.CancellationToken);
        Assert.Equal(FetchInvoiceStatus.Received, payload.Status);
        Assert.Equal(10_000UL, payload.Amount!.MilliSatoshi);
        _service.Verify(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                             It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_AnInvoiceError_When_Paying_Then_ANormalResponseWithoutAPayment()
    {
        // Arrange
        _service.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                            It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PayOfferResult(new FetchInvoiceResult(FetchInvoiceStatus.InvoiceError, null, 1,
                                                                        "out of stock", 86), null));
        var handler = GetHandler(ClientCommand.PayOffer);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.PayOffer,
                                                                new PayOfferIpcRequest { Offer = Offer }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var payload = MessagePackSerializer.Deserialize<PayOfferIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
        Assert.Equal(FetchInvoiceStatus.InvoiceError, payload.Fetch.Status);
        Assert.Equal("out of stock", payload.Fetch.Error);
        Assert.Equal(86UL, payload.Fetch.ErroneousField);
        Assert.Null(payload.Payment);
    }

    [Fact]
    public async Task Given_TheServiceRefusesTheOffer_When_Paying_Then_InvalidOperation()
    {
        // Arrange
        _service.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                            It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentException("B12-OFR-03: the offer expired"));
        var handler = GetHandler(ClientCommand.PayOffer);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.PayOffer,
                                                                new PayOfferIpcRequest { Offer = Offer }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("B12-OFR-03", error.Message);
    }

    [Theory]
    [InlineData(" ", null, null, null)]
    [InlineData(Offer, 0UL, null, null)]
    [InlineData(Offer, null, 0UL, null)]
    [InlineData(Offer, null, null, 301u)]
    public async Task Given_ArgumentsOutOfBounds_When_Paying_Then_RefusedBeforeTheService(string offer,
        ulong? amountMsat, ulong? quantity, uint? timeout)
    {
        // Arrange
        var handler = GetHandler(ClientCommand.PayOffer);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.PayOffer, new PayOfferIpcRequest
        {
            Offer = offer,
            Amount = amountMsat is { } a ? LightningMoney.MilliSatoshis(a) : null,
            Quantity = quantity,
            TimeoutSeconds = timeout
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
        _service.Verify(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                             It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneHandlerPerCommand()
    {
        // Arrange
        var services = BuildServices();
        services.AddOfferSendIpcServices();

        // Act
        var provider = services.BuildServiceProvider();

        // Assert
        Assert.Single(provider.GetServices<IIpcCommandHandler>(), h => h.Command == ClientCommand.PayOffer);
        Assert.Single(provider.GetServices<IIpcCommandHandler>(), h => h.Command == ClientCommand.FetchInvoice);
        Assert.Same(_service.Object, provider.GetRequiredService<IOfferPaymentService>());
    }

    private static PaymentModel Payment()
    {
        var payment = new PaymentModel(s_hash, null, s_node, LightningMoney.MilliSatoshis(10_000),
                                       LightningMoney.MilliSatoshis(5), DateTimeOffset.UnixEpoch);
        payment.Succeed(new Secret(new byte[32]), DateTimeOffset.UnixEpoch);
        return payment;
    }

    private static IpcError AssertError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private IIpcCommandHandler GetHandler(ClientCommand command)
    {
        var provider = BuildServices().BuildServiceProvider();
        return provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == command);
    }

    private ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_service.Object);
        services.AddOfferSendIpcServices();
        return services;
    }

    private static IpcEnvelope CreateEnvelope(ClientCommand command, PayOfferIpcRequest request) =>
        new()
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };
}