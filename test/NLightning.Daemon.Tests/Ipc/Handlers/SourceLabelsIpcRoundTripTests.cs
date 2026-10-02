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
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Offers.Enums;
using Domain.Offers.Interfaces;
using Domain.Offers.Models;
using Domain.Payments.Interfaces;
using Domain.Payments.Keysend;
using Domain.Payments.Models;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The operator's label and tags at the source (NL-602 A3-T1) over IPC: <c>createinvoice</c>, <c>payinvoice</c>,
/// <c>keysend</c>, <c>createoffer</c>, <c>payoffer</c> and <c>withdraw</c> carry them (the requests' new keys) to the
/// services after the shared checks, the list responses carry the stored ones back, a broken rule is refused before any
/// service call, and a request of an older client (without the keys) reads as no label.
/// </summary>
public class SourceLabelsIpcRoundTripTests
{
    private const string Offer = "lno1qgsqvgnwgcg35z6ee2h3yczraddm72xrfua9uve2rlrm9deu7xyfzrc";
    private const string Address = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";

    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly Hash s_hash = new(Enumerable.Repeat((byte)0x77, 32).ToArray());
    private static readonly CompactPubKey s_node =
        new(Convert.FromHexString("0324653eac434488002cc06bbfb7f10fe18991e35f9fe4302dbea6d2353dc0ab1c"));
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_750_000_000);

    private readonly Mock<IInvoiceService> _invoices = new();
    private readonly Mock<IPaymentService> _payments = new();
    private readonly Mock<IOfferService> _offers = new();
    private readonly Mock<IOfferPaymentService> _offerPayments = new();
    private readonly Mock<IWalletSpendService> _wallet = new();

    public SourceLabelsIpcRoundTripTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_ALabelAndTags_When_CreatingAnInvoice_Then_TheServiceGetsThemCheckedAndTheyComeBack()
    {
        // Arrange
        SourceLabels? received = null;
        _invoices.Setup(s => s.CreateInvoiceAsync(It.IsAny<LightningMoney?>(), "tea", null, It.IsAny<SourceLabels>(),
                                                  It.IsAny<CancellationToken>()))
                 .Callback<LightningMoney?, string, uint?, SourceLabels, CancellationToken>(
                      (_, _, _, labels, _) => received = labels)
                 .ReturnsAsync(() => Invoice(received!));
        var handler = new CreateInvoiceIpcHandler(NullLogger<CreateInvoiceIpcHandler>.Instance, BuildProvider());

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.CreateInvoice, new CreateInvoiceIpcRequest
        {
            Amount = LightningMoney.MilliSatoshis(21_000),
            Description = "tea",
            Label = "café order 42",
            Tags = ["project=alpha", "customer=acme"]
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.NotNull(received);
        Assert.Equal("café order 42", received.Label);
        Assert.Equal("customer=acme\nproject=alpha", received.CanonicalTags);
        var invoice = Deserialize<CreateInvoiceIpcResponse>(response).Invoice;
        Assert.Equal("café order 42", invoice.Label);
        Assert.Equal(["customer=acme", "project=alpha"], invoice.Tags);
    }

    [Fact]
    public async Task Given_StoredLabels_When_ListingInvoicesAndPayments_Then_TheyCrossTheWire()
    {
        // Arrange
        var labels = SourceLabels.Create("rent", ["month=2026-10"]);
        _invoices.Setup(s => s.ListInvoicesAsync(0, 10, It.IsAny<CancellationToken>()))
                 .ReturnsAsync([Invoice(labels), Invoice(SourceLabels.None)]);
        _payments.Setup(s => s.ListPaymentsAsync(0, 10, It.IsAny<CancellationToken>()))
                 .ReturnsAsync([Payment(labels)]);
        var provider = BuildProvider();
        var listInvoices = new ListInvoicesIpcHandler(NullLogger<ListInvoicesIpcHandler>.Instance, provider);
        var listPayments = new ListPaymentsIpcHandler(NullLogger<ListPaymentsIpcHandler>.Instance, provider);

        // Act
        var invoices = Deserialize<ListInvoicesIpcResponse>(
            await listInvoices.HandleAsync(CreateEnvelope(ClientCommand.ListInvoices,
                                                          new ListInvoicesIpcRequest { Take = 10 }),
                                           TestContext.Current.CancellationToken));
        var payments = Deserialize<ListPaymentsIpcResponse>(
            await listPayments.HandleAsync(CreateEnvelope(ClientCommand.ListPayments,
                                                          new ListPaymentsIpcRequest { Take = 10 }),
                                           TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("rent", invoices.Invoices[0].Label);
        Assert.Equal(["month=2026-10"], invoices.Invoices[0].Tags);
        Assert.Null(invoices.Invoices[1].Label);
        Assert.Null(invoices.Invoices[1].Tags);
        var payment = Assert.Single(payments.Payments);
        Assert.Equal("rent", payment.Label);
        Assert.Equal(["month=2026-10"], payment.Tags);
    }

    [Fact]
    public async Task Given_ALabelAndTags_When_PayingAnInvoice_Then_ThePaymentOptionsCarryThem()
    {
        // Arrange
        PayInvoiceOptions? options = null;
        _payments.Setup(s => s.PayInvoiceAsync("lnbcrt1pay", null, It.IsAny<PayInvoiceOptions>(),
                                               It.IsAny<CancellationToken>()))
                 .Callback<string, LightningMoney?, PayInvoiceOptions, CancellationToken>((_, _, o, _) => options = o)
                 .ReturnsAsync(() => new PayInvoiceResult(Payment(options!.Labels), 1, 1));
        var handler = new PayInvoiceIpcHandler(NullLogger<PayInvoiceIpcHandler>.Instance, BuildProvider());

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.PayInvoice, new PayInvoiceIpcRequest
        {
            Bolt11 = "lnbcrt1pay",
            Label = "supplier",
            Tags = ["category=supplies"]
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal("supplier", options!.Labels.Label);
        Assert.Equal("category=supplies", options.Labels.CanonicalTags);
        var payment = Deserialize<PayInvoiceIpcResponse>(response).Payment;
        Assert.Equal("supplier", payment.Label);
        Assert.Equal(["category=supplies"], payment.Tags);
    }

    [Fact]
    public async Task Given_ALabelAndTags_When_SendingAKeysend_Then_ThePaymentOptionsCarryThem()
    {
        // Arrange
        PayInvoiceOptions? options = null;
        _payments.Setup(s => s.PayKeysendAsync(It.IsAny<PayKeysendRequest>(), It.IsAny<PayInvoiceOptions>(),
                                               It.IsAny<CancellationToken>()))
                 .Callback<PayKeysendRequest, PayInvoiceOptions, CancellationToken>((_, o, _) => options = o)
                 .ReturnsAsync(() => new PayInvoiceResult(Payment(options!.Labels), 1, 1));
        var handler = GetHandler(ClientCommand.Keysend);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.Keysend, new KeysendIpcRequest
        {
            Destination = s_node,
            Amount = LightningMoney.Satoshis(21),
            Label = "tip",
            Tags = ["podcast=ep-12"]
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal("tip", options!.Labels.Label);
        Assert.Equal("podcast=ep-12", options.Labels.CanonicalTags);
        Assert.Equal("tip", Deserialize<PayInvoiceIpcResponse>(response).Payment.Label);
    }

    [Fact]
    public async Task Given_ALabelAndTags_When_CreatingAnOffer_Then_TheOfferRequestCarriesThemAndTheyComeBack()
    {
        // Arrange
        CreateOfferRequest? request = null;
        _offers.SetupGet(s => s.IsAvailable).Returns(true);
        _offers.Setup(s => s.CreateOfferAsync(It.IsAny<CreateOfferRequest>(), It.IsAny<CancellationToken>()))
               .Callback<CreateOfferRequest, CancellationToken>((r, _) => request = r)
               .ReturnsAsync(() => new CreatedOffer(OfferModel(request!.Labels), null));
        var handler = GetHandler(ClientCommand.CreateOffer);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.CreateOffer, new CreateOfferIpcRequest
        {
            Description = "coffee",
            Label = "shop",
            Tags = ["till=2", "branch=main"]
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal("shop", request!.Labels.Label);
        Assert.Equal("branch=main\ntill=2", request.Labels.CanonicalTags);
        var offer = Deserialize<CreateOfferIpcResponse>(response).Offer;
        Assert.Equal("shop", offer.Label);
        Assert.Equal(["branch=main", "till=2"], offer.Tags);
    }

    [Fact]
    public async Task Given_ALabelAndTags_When_PayingAnOffer_Then_ThePaymentOptionsCarryThem()
    {
        // Arrange
        PayOfferOptions? options = null;
        var fetched = new FetchedBolt12Invoice(new byte[] { 1, 2 }, new byte[] { 3 }, s_node,
                                               LightningMoney.MilliSatoshis(10_000), s_hash,
                                               DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), 7200, 2);
        _offerPayments.Setup(s => s.PayOfferAsync(It.IsAny<PayOfferRequest>(), It.IsAny<PayOfferOptions>(),
                                                  It.IsAny<CancellationToken>()))
                      .Callback<PayOfferRequest, PayOfferOptions, CancellationToken>((_, o, _) => options = o)
                      .ReturnsAsync(() => new PayOfferResult(
                                        new FetchInvoiceResult(FetchInvoiceStatus.Received, fetched, 1),
                                        new PayInvoiceResult(Payment(options!.Payment.Labels), 1, 1)));
        var handler = GetHandler(ClientCommand.PayOffer);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.PayOffer, new PayOfferIpcRequest
        {
            Offer = Offer,
            Amount = LightningMoney.MilliSatoshis(10_000),
            Label = "subscription",
            Tags = ["vendor=acme"]
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal("subscription", options!.Payment.Labels.Label);
        Assert.Equal("vendor=acme", options.Payment.Labels.CanonicalTags);
        Assert.Equal("subscription", Deserialize<PayOfferIpcResponse>(response).Payment!.Label);
    }

    [Fact]
    public async Task Given_ALabelAndTags_When_Withdrawing_Then_TheSpendRequestCarriesThem()
    {
        // Arrange
        WalletWithdrawRequest? request = null;
        _wallet.Setup(s => s.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()))
               .Callback<WalletWithdrawRequest, CancellationToken>((r, _) => request = r)
               .ReturnsAsync(new WalletWithdrawResult(new TxId(new byte[32]), LightningMoney.Satoshis(40_000),
                                                      LightningMoney.Satoshis(566), LightningMoney.Satoshis(0),
                                                      LightningMoney.Satoshis(2_500), 561, 1, LightningMoney.Zero,
                                                      true));
        var handler = GetHandler(ClientCommand.Withdraw);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.Withdraw, new WithdrawIpcRequest
        {
            Address = Address,
            AmountSat = 40_000,
            Label = "cold storage",
            Tags = ["category=savings"]
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal("cold storage", request!.Labels.Label);
        Assert.Equal("category=savings", request.Labels.CanonicalTags);
    }

    [Theory]
    [InlineData(ClientCommand.CreateInvoice)]
    [InlineData(ClientCommand.PayInvoice)]
    [InlineData(ClientCommand.Keysend)]
    [InlineData(ClientCommand.CreateOffer)]
    [InlineData(ClientCommand.PayOffer)]
    [InlineData(ClientCommand.Withdraw)]
    public async Task Given_ABrokenTagRule_When_Handled_Then_RefusedBeforeAnyServiceCall(ClientCommand command)
    {
        // Arrange: an upper-case key and a repeated key; each command refuses with the shared message
        _offers.SetupGet(s => s.IsAvailable).Returns(true);
        var tags = new List<string> { "Customer=acme" };
        var envelope = command switch
        {
            ClientCommand.CreateInvoice => CreateEnvelope(command, new CreateInvoiceIpcRequest { Tags = tags }),
            ClientCommand.PayInvoice => CreateEnvelope(command,
                                                       new PayInvoiceIpcRequest { Bolt11 = "lnbcrt1pay", Tags = tags }),
            ClientCommand.Keysend => CreateEnvelope(command, new KeysendIpcRequest
            {
                Destination = s_node,
                Amount = LightningMoney.Satoshis(21),
                Tags = tags
            }),
            ClientCommand.CreateOffer => CreateEnvelope(command, new CreateOfferIpcRequest { Tags = tags }),
            ClientCommand.PayOffer => CreateEnvelope(command, new PayOfferIpcRequest { Offer = Offer, Tags = tags }),
            _ => CreateEnvelope(command, new WithdrawIpcRequest { Address = Address, AmountSat = 1_000, Tags = tags })
        };
        var provider = BuildProvider();
        var handler = command switch
        {
            ClientCommand.CreateInvoice => new CreateInvoiceIpcHandler(NullLogger<CreateInvoiceIpcHandler>.Instance,
                                                                       provider),
            ClientCommand.PayInvoice => new PayInvoiceIpcHandler(NullLogger<PayInvoiceIpcHandler>.Instance, provider),
            _ => GetHandler(command)
        };

        // Act
        var response = await handler.HandleAsync(envelope, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("Invalid label or tag", error.Message);
        Assert.Contains("Customer", error.Message);
        _invoices.VerifyNoOtherCalls();
        _payments.VerifyNoOtherCalls();
        _offers.Verify(s => s.CreateOfferAsync(It.IsAny<CreateOfferRequest>(), It.IsAny<CancellationToken>()),
                       Times.Never);
        _offerPayments.VerifyNoOtherCalls();
        _wallet.VerifyNoOtherCalls();
    }

    [Fact]
    public void Given_ARequestOfAnOlderClient_When_Deserialized_Then_ItHasNoLabelAndNoTags()
    {
        // Arrange: createinvoice without keys 3 and 4 (a client before A3-T1)
        var current = MessagePackSerializer.Serialize(new CreateInvoiceIpcRequest
        {
            Amount = LightningMoney.MilliSatoshis(1_000),
            Description = "tea",
            ExpirySeconds = 60
        }, s_options, TestContext.Current.CancellationToken);
        Assert.Equal(0x95, current[0]); // fixarray of 5 (keys 0-4)
        Assert.Equal(0xC0, current[^2]); // key 3: nil
        Assert.Equal(0xC0, current[^1]); // key 4: nil
        byte[] older = [0x93, .. current[1..^2]];

        // Act
        var request = MessagePackSerializer.Deserialize<CreateInvoiceIpcRequest>(
                          older, s_options, TestContext.Current.CancellationToken)
                                           .ToClientRequest();

        // Assert
        Assert.Equal("tea", request.Description);
        Assert.Null(request.Label);
        Assert.Empty(request.Tags);
    }

    [Fact]
    public void Given_AnOpenChannelRequestWithLabels_When_MappedToTheClientRequest_Then_KeysSevenAndEightAreKept()
    {
        // Arrange
        var bytes = MessagePackSerializer.Serialize(new OpenChannelIpcRequest
        {
            NodeInfo = "02abc@127.0.0.1:9735",
            Amount = LightningMoney.Satoshis(100_000),
            Label = "routing node",
            Tags = ["peer=acme"]
        }, s_options, TestContext.Current.CancellationToken);

        // Act
        var request = MessagePackSerializer.Deserialize<OpenChannelIpcRequest>(bytes, s_options,
                                                                               TestContext.Current.CancellationToken)
                                           .ToClientRequest();

        // Assert
        Assert.Equal("routing node", request.Label);
        Assert.Equal(["peer=acme"], request.Tags);
    }

    private IServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_invoices.Object);
        services.AddSingleton(_payments.Object);
        services.AddSingleton(_offers.Object);
        services.AddSingleton(_offerPayments.Object);
        services.AddSingleton(_wallet.Object);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IClientCommandHandler<CreateInvoiceClientRequest, CreateInvoiceClientResponse>,
            CreateInvoiceClientHandler>();
        services.AddScoped<IClientCommandHandler<PayInvoiceClientRequest, PayInvoiceClientResponse>,
            PayInvoiceClientHandler>();
        services.AddScoped<IClientCommandHandler<ListInvoicesClientRequest, ListInvoicesClientResponse>,
            ListInvoicesClientHandler>();
        services.AddScoped<IClientCommandHandler<ListPaymentsClientRequest, ListPaymentsClientResponse>,
            ListPaymentsClientHandler>();
        services.AddKeysendIpcServices();
        services.AddOfferIpcServices();
        services.AddOfferSendIpcServices();
        services.AddWithdrawIpcServices();
        return services.BuildServiceProvider();
    }

    private IIpcCommandHandler GetHandler(ClientCommand command) =>
        BuildProvider().GetServices<IIpcCommandHandler>().Single(h => h.Command == command);

    private static InvoiceModel Invoice(SourceLabels labels) =>
        new(s_hash, new Secret(new byte[32]), new Secret(new byte[32]), LightningMoney.MilliSatoshis(21_000), "tea",
            "lnbcrt210n1test", s_now, 900, 40)
        {
            Label = labels.Label,
            Tags = labels.CanonicalTags
        };

    private static PaymentModel Payment(SourceLabels labels) =>
        new(s_hash, "lnbcrt1pay", s_node, LightningMoney.MilliSatoshis(10_000), LightningMoney.Zero, s_now)
        {
            Label = labels.Label,
            Tags = labels.CanonicalTags
        };

    private static OfferModel OfferModel(SourceLabels labels) =>
        new(new Hash(Enumerable.Repeat((byte)1, 32).ToArray()), "lno1qqqq", new byte[] { 1, 2 }, "coffee", null, null,
            null, null, null, new byte[16], OfferIssuerKind.NodeId, true, s_now)
        {
            Label = labels.Label,
            Tags = labels.CanonicalTags
        };

    private static IpcEnvelope CreateEnvelope<T>(ClientCommand command, T request) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };

    private static T Deserialize<T>(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<T>(response.Payload, s_options, TestContext.Current.CancellationToken);
    }
}