using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Labels;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
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
/// The hold invoice IPC handlers (ClientCommand 49-51, NL-995) end to end over MessagePack, with the real client
/// handlers and mocked <see cref="IInvoiceService"/>/<see cref="IHoldInvoiceService"/>.
/// </summary>
public class HoldInvoiceIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_createdAt = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);
    private static readonly Hash s_paymentHash = new(Enumerable.Repeat((byte)0xab, 32).ToArray());
    private static readonly Secret s_preimage = new(Enumerable.Repeat((byte)0xcd, 32).ToArray());

    private readonly Mock<IInvoiceService> _invoiceServiceMock = new();
    private readonly Mock<IHoldInvoiceService> _holdInvoiceServiceMock = new();

    public HoldInvoiceIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_CreateHoldInvoiceRequest_When_HandleAsync_Then_InvoiceCrossesTheWire()
    {
        // Arrange
        _invoiceServiceMock.Setup(x => x.CreateHoldInvoiceAsync(s_paymentHash, It.IsAny<LightningMoney?>(), "tea",
                                                                900U,
                                                                It.Is<SourceLabels>(l => l.Label == "shop"
                                                                                       && l.TagStrings.Contains("a=b")),
                                                                It.IsAny<CancellationToken>()))
                           .ReturnsAsync(CreateHoldInvoice());
        var handler = new CreateHoldInvoiceIpcHandler(NullLogger<CreateHoldInvoiceIpcHandler>.Instance,
                                                      BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.CreateHoldInvoice, new CreateHoldInvoiceIpcRequest
        {
            PaymentHash = s_paymentHash,
            Amount = LightningMoney.MilliSatoshis(21_000),
            Description = "tea",
            ExpirySeconds = 900,
            Label = "shop",
            Tags = ["a=b"]
        });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        var payload = Deserialize<HoldInvoiceIpcResponse>(response);
        Assert.Equal("lnbcrt1hold", payload.Invoice.Bolt11);
        Assert.Equal(s_paymentHash, payload.Invoice.PaymentHash);
        Assert.Equal(21_000UL, payload.Invoice.Amount!.MilliSatoshi);
        Assert.Equal(InvoiceStatus.Open, payload.Invoice.Status);
        Assert.Equal("shop", payload.Invoice.Label);
        Assert.Equal(["a=b"], payload.Invoice.Tags);
        _invoiceServiceMock.Verify(x => x.CreateHoldInvoiceAsync(
                                       s_paymentHash,
                                       It.Is<LightningMoney?>(a => a!.MilliSatoshi == 21_000), "tea", 900U,
                                       It.Is<SourceLabels>(l => l.Label == "shop" && l.TagStrings.Contains("a=b")),
                                       It.IsAny<CancellationToken>()),
                                   Times.Once);
    }

    [Fact]
    public async Task Given_SettleHoldInvoiceRequest_When_HandleAsync_Then_SettledInvoiceCrossesTheWire()
    {
        // Arrange
        _holdInvoiceServiceMock.Setup(x => x.SettleHoldInvoiceAsync(s_paymentHash, s_preimage,
                                                                    It.IsAny<CancellationToken>()))
                               .ReturnsAsync(CreateHeldInvoice().SettleForTest(s_preimage));
        var handler = new SettleHoldInvoiceIpcHandler(NullLogger<SettleHoldInvoiceIpcHandler>.Instance,
                                                      BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.SettleHoldInvoice,
                                      new SettleHoldInvoiceIpcRequest { PaymentHash = s_paymentHash, Preimage = s_preimage });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        var payload = Deserialize<HoldInvoiceIpcResponse>(response);
        Assert.Equal(InvoiceStatus.Settled, payload.Invoice.Status);
        Assert.Equal(21_000UL, payload.Invoice.AmountReceived!.MilliSatoshi);
        Assert.NotNull(payload.Invoice.SettledAt);
        _holdInvoiceServiceMock.Verify(x => x.SettleHoldInvoiceAsync(s_paymentHash, s_preimage,
                                                                     It.IsAny<CancellationToken>()),
                                       Times.Once);
    }

    [Fact]
    public async Task Given_CancelHoldInvoiceRequest_When_HandleAsync_Then_CanceledInvoiceCrossesTheWire()
    {
        // Arrange
        _holdInvoiceServiceMock.Setup(x => x.CancelHoldInvoiceAsync(s_paymentHash,
                                                                    It.IsAny<CancellationToken>()))
                               .ReturnsAsync(CreateHoldInvoice().CancelForTest());
        var handler = new CancelHoldInvoiceIpcHandler(NullLogger<CancelHoldInvoiceIpcHandler>.Instance,
                                                      BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.CancelHoldInvoice,
                                      new CancelHoldInvoiceIpcRequest { PaymentHash = s_paymentHash });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        AssertResponseEnvelope(envelope, response);
        var payload = Deserialize<HoldInvoiceIpcResponse>(response);
        Assert.Equal(InvoiceStatus.Canceled, payload.Invoice.Status);
        Assert.Null(payload.Invoice.SettledAt);
        _holdInvoiceServiceMock.Verify(x => x.CancelHoldInvoiceAsync(s_paymentHash, It.IsAny<CancellationToken>()),
                                       Times.Once);
    }

    [Fact]
    public async Task Given_ZeroAmountCreateRequest_When_HandleAsync_Then_ClientErrorCodeIsReturned()
    {
        // Arrange
        var handler = new CreateHoldInvoiceIpcHandler(NullLogger<CreateHoldInvoiceIpcHandler>.Instance,
                                                      BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.CreateHoldInvoice, new CreateHoldInvoiceIpcRequest
        {
            PaymentHash = s_paymentHash,
            Amount = LightningMoney.Zero
        });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("amount", error.Message);
        _invoiceServiceMock.Verify(x => x.CreateHoldInvoiceAsync(It.IsAny<Hash>(), It.IsAny<LightningMoney?>(),
                                                                 It.IsAny<string>(), It.IsAny<uint?>(),
                                                                 It.IsAny<SourceLabels>(),
                                                                 It.IsAny<CancellationToken>()),
                                   Times.Never);
    }

    [Fact]
    public async Task Given_CreateRequestTheServiceRejects_When_HandleAsync_Then_RejectionCrossesTheWire()
    {
        // Arrange: a BOLT 11 invoice already exists for the hash (the service's ArgumentException)
        _invoiceServiceMock.Setup(x => x.CreateHoldInvoiceAsync(It.IsAny<Hash>(), It.IsAny<LightningMoney?>(),
                                                                It.IsAny<string>(), It.IsAny<uint?>(),
                                                                It.IsAny<SourceLabels>(),
                                                                It.IsAny<CancellationToken>()))
                           .ThrowsAsync(new ArgumentException("An invoice already exists for this payment hash.",
                                                              "paymentHash"));
        var handler = new CreateHoldInvoiceIpcHandler(NullLogger<CreateHoldInvoiceIpcHandler>.Instance,
                                                      BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.CreateHoldInvoice,
                                      new CreateHoldInvoiceIpcRequest { PaymentHash = s_paymentHash });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("Invalid hold invoice", error.Message);
        Assert.Contains("already exists", error.Message);
    }

    [Fact]
    public async Task Given_WrongStatusSettle_When_HandleAsync_Then_InvalidOperationIsReturned()
    {
        // Arrange: the invoice is no longer Held, the contract's InvalidOperationException
        _holdInvoiceServiceMock.Setup(x => x.SettleHoldInvoiceAsync(s_paymentHash, s_preimage,
                                                                    It.IsAny<CancellationToken>()))
                               .ThrowsAsync(new InvalidOperationException("The hold invoice is Canceled, not Held."));
        var handler = new SettleHoldInvoiceIpcHandler(NullLogger<SettleHoldInvoiceIpcHandler>.Instance,
                                                      BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.SettleHoldInvoice,
                                      new SettleHoldInvoiceIpcRequest { PaymentHash = s_paymentHash, Preimage = s_preimage });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal("The hold invoice is Canceled, not Held.", error.Message);
    }

    [Fact]
    public async Task Given_SettleOfAnUnknownHash_When_HandleAsync_Then_InvalidOperationIsReturned()
    {
        // Arrange: no invoice for the hash, the contract's ArgumentException
        _holdInvoiceServiceMock.Setup(x => x.SettleHoldInvoiceAsync(It.IsAny<Hash>(), It.IsAny<Secret>(),
                                                                    It.IsAny<CancellationToken>()))
                               .ThrowsAsync(new ArgumentException("No invoice for payment hash abab."));
        var handler = new SettleHoldInvoiceIpcHandler(NullLogger<SettleHoldInvoiceIpcHandler>.Instance,
                                                      BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.SettleHoldInvoice,
                                      new SettleHoldInvoiceIpcRequest { PaymentHash = s_paymentHash, Preimage = s_preimage });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("Invalid settle", error.Message);
    }

    [Fact]
    public async Task Given_CancelOfSettledInvoice_When_HandleAsync_Then_InvalidOperationIsReturned()
    {
        // Arrange
        _holdInvoiceServiceMock.Setup(x => x.CancelHoldInvoiceAsync(It.IsAny<Hash>(),
                                                                    It.IsAny<CancellationToken>()))
                               .ThrowsAsync(new InvalidOperationException(
                                   "The invoice is Settled; only an open or held one can be canceled."));
        var handler = new CancelHoldInvoiceIpcHandler(NullLogger<CancelHoldInvoiceIpcHandler>.Instance,
                                                      BuildProvider());
        var envelope = CreateEnvelope(ClientCommand.CancelHoldInvoice,
                                      new CancelHoldInvoiceIpcRequest { PaymentHash = s_paymentHash });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("only an open or held one can be canceled", error.Message);
    }

    [Fact]
    public async Task Given_MissingHashOrPreimage_When_Handled_Then_ClientExceptionNamesIt()
    {
        // Arrange: the wire cannot type-check a default Hash/Secret, so the client handler guards them
        var createHandler = new CreateHoldInvoiceClientHandler(_invoiceServiceMock.Object, TimeProvider.System);
        var settleHandler = new SettleHoldInvoiceClientHandler(_holdInvoiceServiceMock.Object, TimeProvider.System);
        var cancelHandler = new CancelHoldInvoiceClientHandler(_holdInvoiceServiceMock.Object, TimeProvider.System);

        // Act / Assert
        var createError = await Assert.ThrowsAsync<ClientException>(() =>
            createHandler.HandleAsync(new CreateHoldInvoiceClientRequest { PaymentHash = default },
                                      TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidOperation, createError.ErrorCode);
        Assert.Contains("payment hash", createError.Message);

        var settleHashError = await Assert.ThrowsAsync<ClientException>(() =>
            settleHandler.HandleAsync(new SettleHoldInvoiceClientRequest { PaymentHash = default, Preimage = s_preimage },
                                      TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidOperation, settleHashError.ErrorCode);
        Assert.Contains("payment hash", settleHashError.Message);

        var settlePreimageError = await Assert.ThrowsAsync<ClientException>(() =>
            settleHandler.HandleAsync(new SettleHoldInvoiceClientRequest { PaymentHash = s_paymentHash, Preimage = default },
                                      TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidOperation, settlePreimageError.ErrorCode);
        Assert.Contains("preimage", settlePreimageError.Message);

        var cancelError = await Assert.ThrowsAsync<ClientException>(() =>
            cancelHandler.HandleAsync(new CancelHoldInvoiceClientRequest { PaymentHash = default },
                                      TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.InvalidOperation, cancelError.ErrorCode);
        Assert.Contains("payment hash", cancelError.Message);
    }

    [Fact]
    public async Task Given_NodeWithoutHoldInvoiceService_When_HandleAsync_Then_NotAvailableIsReturned()
    {
        // Arrange: the client handler is registered like the node does it, but IHoldInvoiceService is not
        var services = new ServiceCollection();
        services.AddScoped<IClientCommandHandler<SettleHoldInvoiceClientRequest, HoldInvoiceClientResponse>>(sp =>
            new SettleHoldInvoiceClientHandler(
                NodeServiceExtensions.GetPaymentLayerService<IHoldInvoiceService>(sp),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(TimeProvider.System);
        var handler = new SettleHoldInvoiceIpcHandler(NullLogger<SettleHoldInvoiceIpcHandler>.Instance,
                                                     services.BuildServiceProvider());
        var envelope = CreateEnvelope(ClientCommand.SettleHoldInvoice,
                                      new SettleHoldInvoiceIpcRequest { PaymentHash = s_paymentHash, Preimage = s_preimage });

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(envelope, response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("not available", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(nameof(IHoldInvoiceService), error.Message);
    }

    [Fact]
    public void Given_Handlers_When_CommandRead_Then_EachServesItsClientCommand()
    {
        // Arrange: 48 stays free for payroute (NL-1082), so these pin 49-51
        var provider = BuildProvider();
        IIpcCommandHandler[] handlers =
        [
            new CreateHoldInvoiceIpcHandler(NullLogger<CreateHoldInvoiceIpcHandler>.Instance, provider),
            new SettleHoldInvoiceIpcHandler(NullLogger<SettleHoldInvoiceIpcHandler>.Instance, provider),
            new CancelHoldInvoiceIpcHandler(NullLogger<CancelHoldInvoiceIpcHandler>.Instance, provider)
        ];

        // Act
        var commands = handlers.Select(h => h.Command).ToArray();

        // Assert
        Assert.Equal([
            ClientCommand.CreateHoldInvoice, ClientCommand.SettleHoldInvoice, ClientCommand.CancelHoldInvoice
        ], commands);
        Assert.Equal([49, 50, 51], commands.Select(c => (int)c));
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_invoiceServiceMock.Object);
        services.AddSingleton(_holdInvoiceServiceMock.Object);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IClientCommandHandler<CreateHoldInvoiceClientRequest, HoldInvoiceClientResponse>,
            CreateHoldInvoiceClientHandler>();
        services.AddScoped<IClientCommandHandler<SettleHoldInvoiceClientRequest, HoldInvoiceClientResponse>,
            SettleHoldInvoiceClientHandler>();
        services.AddScoped<IClientCommandHandler<CancelHoldInvoiceClientRequest, HoldInvoiceClientResponse>,
            CancelHoldInvoiceClientHandler>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>An open hold invoice: no preimage, that is the whole point (NL-995).</summary>
    private static InvoiceModel CreateHoldInvoice() =>
        new(s_paymentHash, null, new Secret(new byte[32]), LightningMoney.MilliSatoshis(21_000), "tea", "lnbcrt1hold",
            s_createdAt, 900, 40)
        {
            Label = "shop",
            Tags = "a=b"
        };

    /// <summary>A held hold invoice: the paying HTLC set completed and is locked in.</summary>
    private static InvoiceModel CreateHeldInvoice() =>
        new(s_paymentHash, null, new Secret(new byte[32]), LightningMoney.MilliSatoshis(21_000), "tea", "lnbcrt1hold",
            s_createdAt, 900, 40, InvoiceStatus.Held, LightningMoney.MilliSatoshis(21_000));

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

/// <summary>Test-only transitions of a hold invoice, named for what each test asserts.</summary>
internal static class HoldInvoiceTestExtensions
{
    internal static InvoiceModel SettleForTest(this InvoiceModel invoice, Secret preimage)
    {
        invoice.SettleHeld(preimage, s_settledAt);
        return invoice;
    }

    internal static InvoiceModel CancelForTest(this InvoiceModel invoice)
    {
        invoice.Cancel();
        return invoice;
    }

    private static readonly DateTimeOffset s_settledAt = DateTimeOffset.FromUnixTimeSeconds(1_750_000_060);
}