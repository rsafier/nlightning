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
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>createoffer</c>, <c>listoffers</c> and <c>disableoffer</c> (ClientCommand 26-28, wave B12 lane D)
/// over IPC: requests reach <see cref="IOfferService"/>, results come back, refusals carry
/// <see cref="ErrorCodes.InvalidOperation"/>.
/// </summary>
public class OfferIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_created = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly Mock<IOfferService> _offers = new();
    private CreateOfferRequest? _created;

    public OfferIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _offers.SetupGet(o => o.IsAvailable).Returns(true);
        _offers.Setup(o => o.CreateOfferAsync(It.IsAny<CreateOfferRequest>(), It.IsAny<CancellationToken>()))
               .Callback<CreateOfferRequest, CancellationToken>((r, _) => _created = r)
               .ReturnsAsync((CreateOfferRequest r, CancellationToken _) => Offer(r.Amount, r.Description));
        _offers.Setup(o => o.GetInvoiceCountsAsync(It.IsAny<Hash>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new OfferInvoiceCounts(2, 3));
    }

    private static OfferModel Offer(LightningMoney? amount = null, string? description = "coffee", byte id = 1,
                                    DateTimeOffset? expiry = null) =>
        new(new Hash(Enumerable.Repeat(id, 32).ToArray()), "lno1qqqq", new byte[] { 1, 2 }, description, amount, null,
            null, null, expiry, new byte[16], OfferIssuerKind.NodeId, true, s_created);

    [Fact]
    public async Task Given_ACreateOfferRequest_When_Handled_Then_TheServiceGetsItAndTheOfferComesBack()
    {
        // Arrange
        var handler = GetHandler(ClientCommand.CreateOffer);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(ClientCommand.CreateOffer, new CreateOfferIpcRequest
                           {
                               Amount = LightningMoney.Satoshis(10_000),
                               Description = "coffee",
                               Issuer = "nltg",
                               QuantityMax = 0,
                               AbsoluteExpiry = 1_900_000_000,
                               ForcePaths = true
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<CreateOfferIpcResponse>(response.Payload, s_options,
                                                                                TestContext.Current.CancellationToken);
        Assert.Equal("lno1qqqq", payload.Offer.Bolt12);
        Assert.Equal(LightningMoney.Satoshis(10_000), payload.Offer.Amount);
        Assert.Equal(OfferStatus.Active, payload.Offer.Status);
        Assert.True(payload.Offer.HasPaths);
        Assert.Equal(0, payload.Offer.PaidInvoices);
        Assert.NotNull(_created);
        Assert.Equal("nltg", _created.Issuer);
        Assert.Equal(0UL, _created.QuantityMax);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000), _created.AbsoluteExpiry);
        Assert.True(_created.ForcePaths);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("zero amount")]
    [InlineData("argument")]
    [InlineData("no peer")]
    [InlineData("expiry out of range")]
    public async Task Given_ARefusedCreate_When_Handled_Then_InvalidOperation(string variant)
    {
        // Arrange
        var request = new CreateOfferIpcRequest { Description = "x" };
        switch (variant)
        {
            case "unavailable":
                _offers.SetupGet(o => o.IsAvailable).Returns(false);
                break;
            case "zero amount":
                request = new CreateOfferIpcRequest { Amount = LightningMoney.Zero, Description = "x" };
                break;
            case "argument":
                _offers.Setup(o => o.CreateOfferAsync(It.IsAny<CreateOfferRequest>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new ArgumentException("in the past"));
                break;
            case "no peer":
                _offers.Setup(o => o.CreateOfferAsync(It.IsAny<CreateOfferRequest>(), It.IsAny<CancellationToken>()))
                       .ThrowsAsync(new InvalidOperationException("no peer"));
                break;
            default:
                request = new CreateOfferIpcRequest { AbsoluteExpiry = ulong.MaxValue };
                break;
        }

        // Act
        var response = await GetHandler(ClientCommand.CreateOffer)
                          .HandleAsync(CreateEnvelope(ClientCommand.CreateOffer, request),
                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
    }

    [Fact]
    public async Task Given_AListRequest_When_Handled_Then_TheOffersComeBackWithTheirCounts()
    {
        // Arrange
        _offers.Setup(o => o.ListOffersAsync(true, 5, 10, It.IsAny<CancellationToken>()))
               .ReturnsAsync([Offer(id: 1), Offer(id: 2)]);

        // Act
        var response = await GetHandler(ClientCommand.ListOffers)
                          .HandleAsync(CreateEnvelope(ClientCommand.ListOffers,
                                                      new ListOffersIpcRequest
                                                      {
                                                          ActiveOnly = true,
                                                          Skip = 5,
                                                          Take = 10
                                                      }), TestContext.Current.CancellationToken);

        // Assert
        var payload = MessagePackSerializer.Deserialize<ListOffersIpcResponse>(response.Payload, s_options,
                                                                               TestContext.Current.CancellationToken);
        Assert.Equal(2, payload.Offers.Count);
        Assert.All(payload.Offers, o => Assert.Equal((2, 3), (o.PaidInvoices, o.UnpaidInvoices)));
    }

    [Fact]
    public async Task Given_AnInvalidPage_When_Listing_Then_InvalidOperation()
    {
        // Act
        var response = await GetHandler(ClientCommand.ListOffers)
                          .HandleAsync(CreateEnvelope(ClientCommand.ListOffers,
                                                      new ListOffersIpcRequest { Take = 0 }),
                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
    }

    [Fact]
    public async Task Given_AnActiveOffer_When_Disabled_Then_ChangedAndDisabled()
    {
        // Arrange
        var offer = Offer();
        var disabled = Offer();
        disabled.Disable(s_created.AddHours(1));
        _offers.Setup(o => o.GetOfferAsync(offer.OfferId, It.IsAny<CancellationToken>())).ReturnsAsync(offer);
        _offers.Setup(o => o.DisableOfferAsync(offer.OfferId, It.IsAny<CancellationToken>())).ReturnsAsync(disabled);

        // Act
        var response = await GetHandler(ClientCommand.DisableOffer)
                          .HandleAsync(CreateEnvelope(ClientCommand.DisableOffer,
                                                      new DisableOfferIpcRequest { OfferId = offer.OfferId }),
                                       TestContext.Current.CancellationToken);

        // Assert
        var payload = MessagePackSerializer.Deserialize<DisableOfferIpcResponse>(response.Payload, s_options,
                                                                                 TestContext.Current.CancellationToken);
        Assert.True(payload.Changed);
        Assert.Equal(OfferStatus.Disabled, payload.Offer.Status);
        Assert.False(payload.Offer.IsActive);
        Assert.Equal(s_created.AddHours(1), payload.Offer.DisabledAt);
    }

    [Fact]
    public async Task Given_AnUnknownOffer_When_Disabled_Then_InvalidOperation()
    {
        // Act
        var response = await GetHandler(ClientCommand.DisableOffer)
                          .HandleAsync(CreateEnvelope(ClientCommand.DisableOffer,
                                                      new DisableOfferIpcRequest { OfferId = new Hash(new byte[32]) }),
                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
    }

    [Fact]
    public async Task Given_NoOfferService_When_Called_Then_NotAvailable()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOfferIpcServices();
        var handler = services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                              .Single(h => h.Command == ClientCommand.ListOffers);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(ClientCommand.ListOffers,
                                                                new ListOffersIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneHandlerPerCommand()
    {
        // Arrange
        var services = BuildServices();
        services.AddOfferIpcServices();

        // Act
        var commands = services.BuildServiceProvider().GetServices<IIpcCommandHandler>().Select(h => h.Command)
                               .ToList();

        // Assert
        Assert.Equal([ClientCommand.CreateOffer, ClientCommand.ListOffers,
                      ClientCommand.DisableOffer], commands);
        Assert.Equal([(ClientCommand)26, (ClientCommand)27, (ClientCommand)28], commands);
    }

    private static IpcError AssertError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private IIpcCommandHandler GetHandler(ClientCommand command) =>
        BuildServices().BuildServiceProvider().GetServices<IIpcCommandHandler>().Single(h => h.Command == command);

    private ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_offers.Object);
        services.AddSingleton(TimeProvider.System);
        services.AddOfferIpcServices();
        return services;
    }

    private static IpcEnvelope CreateEnvelope<T>(ClientCommand command, T request) => new()
    {
        Version = 1,
        Command = command,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };
}