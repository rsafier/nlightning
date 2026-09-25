using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The invoice/payment IPC handlers (ClientCommand 9-12) end to end over MessagePack, with the real client handlers
/// and mocked <see cref="IInvoiceService"/>/<see cref="IPaymentService"/>.
/// </summary>
public class PaymentsIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());
    private static readonly CompactPubKey s_payee = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);

    private readonly Mock<IInvoiceService> _invoiceServiceMock = new();
    private readonly Mock<IPaymentService> _paymentServiceMock = new();

    public PaymentsIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_CreateInvoiceRequest_When_HandleAsync_Then_InvoiceCrossesTheWire()
    {
        // Arrange
        _invoiceServiceMock.Setup(x => x.CreateInvoiceAsync(It.IsAny<LightningMoney?>(), "tea", 900U,
                                                            It.IsAny<CancellationToken>()))
                           .ReturnsAsync(CreateInvoice());
        var handler = new CreateInvoiceIpcHandler(NullLogger<CreateInvoiceIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.CreateInvoice, new CreateInvoiceIpcRequest
        {
            Amount = LightningMoney.MilliSatoshis(21_000),
            Description = "tea",
            ExpirySeconds = 900
        });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        var payload = Deserialize<CreateInvoiceIpcResponse>(response);
        Assert.Equal("lnbcrt210n1test", payload.Invoice.Bolt11);
        Assert.Equal(s_paymentHash, payload.Invoice.PaymentHash);
        Assert.Equal(21_000UL, payload.Invoice.Amount!.MilliSatoshi);
        Assert.Equal(InvoiceStatus.Open, payload.Invoice.Status);
        _invoiceServiceMock.Verify(x => x.CreateInvoiceAsync(
                                       It.Is<LightningMoney?>(a => a!.MilliSatoshi == 21_000), "tea", 900U,
                                       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_PayInvoiceRequest_When_HandleAsync_Then_PaymentCrossesTheWire()
    {
        // Arrange
        var payment = CreatePayment();
        payment.AddOutgoingHtlc(new byte[32], 0);
        payment.Succeed(s_preimage, s_now.AddSeconds(1));
        _paymentServiceMock.Setup(x => x.PayInvoiceAsync("lnbcrt1pay", null, TimeSpan.FromSeconds(45),
                                                         It.IsAny<CancellationToken>()))
                           .ReturnsAsync(payment);
        var handler = new PayInvoiceIpcHandler(NullLogger<PayInvoiceIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayInvoice,
                                      new PayInvoiceIpcRequest { Bolt11 = "lnbcrt1pay", TimeoutSeconds = 45 });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        var payload = Deserialize<PayInvoiceIpcResponse>(response);
        Assert.Equal(PaymentStatus.Succeeded, payload.Payment.Status);
        Assert.Equal(s_preimage, payload.Payment.Preimage);
        Assert.Equal(s_payee, payload.Payment.PayeeNodeId);
        Assert.Equal(0UL, payload.Payment.OutgoingHtlcId);
    }

    [Fact]
    public async Task Given_ListInvoicesRequest_When_HandleAsync_Then_InvoicesCrossTheWire()
    {
        // Arrange
        _invoiceServiceMock.Setup(x => x.ListInvoicesAsync(2, 3, It.IsAny<CancellationToken>()))
                           .ReturnsAsync([CreateInvoice(), CreateInvoice()]);
        var handler = new ListInvoicesIpcHandler(NullLogger<ListInvoicesIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.ListInvoices, new ListInvoicesIpcRequest { Skip = 2, Take = 3 });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        Assert.Equal(2, Deserialize<ListInvoicesIpcResponse>(response).Invoices.Count);
    }

    [Fact]
    public async Task Given_ListPaymentsRequest_When_HandleAsync_Then_PaymentsCrossTheWire()
    {
        // Arrange
        _paymentServiceMock.Setup(x => x.ListPaymentsAsync(0, 100, It.IsAny<CancellationToken>()))
                           .ReturnsAsync([CreatePayment()]);
        var handler = new ListPaymentsIpcHandler(NullLogger<ListPaymentsIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.ListPayments, new ListPaymentsIpcRequest());

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        var payment = Assert.Single(Deserialize<ListPaymentsIpcResponse>(response).Payments);
        Assert.Equal(PaymentStatus.InFlight, payment.Status);
    }

    [Fact]
    public async Task Given_InvalidPage_When_HandleAsync_Then_ClientErrorCodeIsReturned()
    {
        // Arrange
        var handler = new ListPaymentsIpcHandler(NullLogger<ListPaymentsIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.ListPayments, new ListPaymentsIpcRequest { Take = 0 });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("take", error.Message);
    }

    [Fact]
    public async Task Given_PaymentAlreadyInFlight_When_HandleAsync_Then_InvalidOperationIsReturned()
    {
        // Arrange
        _paymentServiceMock.Setup(x => x.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                                         It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new InvalidOperationException("already in flight"));
        var handler = new PayInvoiceIpcHandler(NullLogger<PayInvoiceIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.PayInvoice, new PayInvoiceIpcRequest { Bolt11 = "lnbcrt1pay" });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal("already in flight", error.Message);
    }

    [Fact]
    public async Task Given_ServiceFailsUnexpectedly_When_HandleAsync_Then_ServerErrorIsReturned()
    {
        // Arrange
        _invoiceServiceMock.Setup(x => x.ListInvoicesAsync(It.IsAny<int>(), It.IsAny<int>(),
                                                           It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new IOException("db down"));
        var handler = new ListInvoicesIpcHandler(NullLogger<ListInvoicesIpcHandler>.Instance, BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.ListInvoices, new ListInvoicesIpcRequest());

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.ServerError, error.Code);
        Assert.Contains("db down", error.Message);
    }

    [Fact]
    public async Task Given_NodeWithoutPaymentServices_When_HandleAsync_Then_NotAvailableIsReturned()
    {
        // Arrange: the client handler is registered but IPaymentService is not (payments not wired yet)
        var services = new ServiceCollection();
        services.AddScoped<IClientCommandHandler<PayInvoiceClientRequest, PayInvoiceClientResponse>,
            PayInvoiceClientHandler>();
        var handler = new PayInvoiceIpcHandler(NullLogger<PayInvoiceIpcHandler>.Instance,
                                               services.BuildServiceProvider());
        var envelope = CreateEnvelope(ClientCommand.PayInvoice, new PayInvoiceIpcRequest { Bolt11 = "lnbcrt1pay" });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("not available", error.Message);
    }

    [Fact]
    public void Given_Handlers_When_CommandRead_Then_EachServesItsClientCommand()
    {
        // Arrange
        var provider = BuildProvider();
        IIpcCommandHandler[] handlers =
        [
            new CreateInvoiceIpcHandler(NullLogger<CreateInvoiceIpcHandler>.Instance, provider),
            new PayInvoiceIpcHandler(NullLogger<PayInvoiceIpcHandler>.Instance, provider),
            new ListInvoicesIpcHandler(NullLogger<ListInvoicesIpcHandler>.Instance, provider),
            new ListPaymentsIpcHandler(NullLogger<ListPaymentsIpcHandler>.Instance, provider)
        ];

        // Act
        var commands = handlers.Select(h => h.Command).ToArray();

        // Assert
        Assert.Equal([
            ClientCommand.CreateInvoice, ClientCommand.PayInvoice, ClientCommand.ListInvoices,
            ClientCommand.ListPayments
        ], commands);
        Assert.Equal([9, 10, 11, 12], commands.Select(c => (int)c));
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_invoiceServiceMock.Object);
        services.AddSingleton(_paymentServiceMock.Object);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IClientCommandHandler<CreateInvoiceClientRequest, CreateInvoiceClientResponse>,
            CreateInvoiceClientHandler>();
        services.AddScoped<IClientCommandHandler<PayInvoiceClientRequest, PayInvoiceClientResponse>,
            PayInvoiceClientHandler>();
        services.AddScoped<IClientCommandHandler<ListInvoicesClientRequest, ListInvoicesClientResponse>,
            ListInvoicesClientHandler>();
        services.AddScoped<IClientCommandHandler<ListPaymentsClientRequest, ListPaymentsClientResponse>,
            ListPaymentsClientHandler>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static InvoiceModel CreateInvoice() =>
        new(s_paymentHash, s_preimage, new Secret(new byte[32]), LightningMoney.MilliSatoshis(21_000), "tea",
            "lnbcrt210n1test", DateTimeOffset.UtcNow, 900, 40);

    private static PaymentModel CreatePayment() =>
        new(s_paymentHash, "lnbcrt1pay", s_payee, LightningMoney.MilliSatoshis(10_000), LightningMoney.Zero, s_now);

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