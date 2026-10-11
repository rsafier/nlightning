using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Offers.Send;

using Application.Offers.Send;
using Domain.Money;
using Domain.Node.Options;
using Domain.Offers.Enums;
using Domain.Offers.Models;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;
using Domain.Protocol.OnionMessages.Enums;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.ValueObjects;
using static OfferSendTestData;

/// <summary>
/// BOLT 12 plan B4-T3: <see cref="OfferPaymentService"/> fetches the invoice over onion messages (retries over the
/// offer's paths, invoice_error, timeout) and pays it through <see cref="IPaymentService.PayBlindedAsync"/>.
/// </summary>
public class OfferPaymentServiceTests
{
    private readonly TestOfferIssuer _issuer = new();
    private readonly TestBolt12Signer _payer = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
    private readonly Mock<IOnionMessageService> _onionMessages = new();
    private readonly Mock<IPaymentService> _payments = new();
    private readonly List<(OnionMessageDestination Destination, OnionMessageContents Contents)> _sent = [];
    private readonly Queue<Func<byte[], OnionMessageSendResult>> _answers = new();
    private PayBlindedRequest? _paid;

    public OfferPaymentServiceTests()
    {
        _onionMessages.SetupGet(s => s.IsAvailable).Returns(true);
        _onionMessages.Setup(s => s.SendAndWaitForReplyAsync(It.IsAny<OnionMessageDestination>(),
                                                             It.IsAny<OnionMessageContents>(),
                                                             It.IsAny<IReadOnlyCollection<ulong>>(),
                                                             It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                      .Returns((OnionMessageDestination destination, OnionMessageContents contents,
                                IReadOnlyCollection<ulong> types, TimeSpan _, CancellationToken _) =>
                      {
                          Assert.Equal([OnionMessageConstants.InvoiceType, OnionMessageConstants.InvoiceErrorType],
                                       types.Order());
                          _sent.Add((destination, contents));
                          var request = Assert.Single(contents.Records);
                          Assert.Equal(OnionMessageConstants.InvoiceRequestType, request.Type);
                          return Task.FromResult(_answers.Dequeue()(request.Value.ToArray()));
                      });
        _payments.Setup(p => p.PayBlindedAsync(It.IsAny<PayBlindedRequest>(), It.IsAny<PayInvoiceOptions>(),
                                               It.IsAny<CancellationToken>()))
                 .Returns((PayBlindedRequest request, PayInvoiceOptions _, CancellationToken _) =>
                 {
                     _paid = request;
                     return Task.FromResult(new PayInvoiceResult(
                                                new PaymentModel(request.PaymentHash, null, request.PayeeNodeId!.Value,
                                                                 request.Amount, LightningMoney.Zero, Now), 1, 1));
                 });
    }

    [Fact]
    public async Task Given_TheIssuerAnswersWithAnInvoice_When_PayingTheOffer_Then_ThePaymentGoesOverItsPaths()
    {
        // Arrange
        var offer = _issuer.CreateOffer(chain: Chain);
        var path = PaymentPath(Key(0x10));
        _answers.Enqueue(Invoice([path], features: [0x02, 0x00, 0x00]));

        // Act
        var result = await Service().PayOfferAsync(new PayOfferRequest(offer, PayerNote: "tip"), new PayOfferOptions(),
                                                   TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Received, result.Fetch.Status);
        Assert.Equal(1, result.Fetch.Attempts);
        Assert.NotNull(result.Payment);
        Assert.Equal(_issuer.NodeId, Assert.Single(_sent).Destination.NodeId);
        Assert.NotNull(_paid);
        Assert.Equal(PaymentHash, _paid!.PaymentHash);
        Assert.Equal(LightningMoney.MilliSatoshis(10_000), _paid.Amount);
        Assert.Equal(_issuer.NodeId, _paid.PayeeNodeId);
        Assert.True(_paid.AllowMpp);
        Assert.Equal(path.Path.FirstNodeId, Assert.Single(_paid.Paths).Path.FirstNodeId);
        Assert.NotNull(_paid.Bolt12);
        Assert.Equal(offer, _paid.Bolt12!.Offer);
        Assert.Equal("tip", _paid.Bolt12.PayerNote);
        Assert.Equal(result.Fetch.Invoice!.InvoiceBytes.ToArray(), _paid.Bolt12.InvoiceBytes.ToArray());
        Assert.Equal(32, _paid.Bolt12.InvoiceRequestMetadata.Length);
    }

    [Fact]
    public async Task Given_AnOfferWithPaths_When_TheFirstPathTimesOut_Then_TheNextPathIsTried()
    {
        // Arrange: B12-OFR-04 through offer_paths; BOLT 4 OM-S-07 retry over another path
        var first = MessagePath(Key(0x05), Key(0x06));
        var second = MessagePath(Key(0x07), Key(0x08));
        var offer = _issuer.CreateOffer(chain: Chain, paths: [first, second]);
        _answers.Enqueue(_ => new OnionMessageSendResult(OnionMessageSendStatus.ReplyTimedOut));
        _answers.Enqueue(Invoice([PaymentPath(Key(0x10))]));

        // Act
        var result = await Service().FetchInvoiceAsync(new PayOfferRequest(offer), new PayOfferOptions(),
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Received, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(first.FirstPathKey, _sent[0].Destination.BlindedPath!.FirstPathKey);
        Assert.Equal(second.FirstPathKey, _sent[1].Destination.BlindedPath!.FirstPathKey);
        Assert.Null(_paid);
    }

    [Fact]
    public async Task Given_AnInvoiceError_When_Fetching_Then_ItEndsTheFetchWithItsText()
    {
        // Arrange
        var offer = _issuer.CreateOffer(chain: Chain);
        _answers.Enqueue(_ => Reply(OnionMessageConstants.InvoiceErrorType,
                                    TestOfferIssuer.CreateInvoiceError("out of stock", 86)));

        // Act
        var result = await Service().PayOfferAsync(new PayOfferRequest(offer), new PayOfferOptions(),
                                                   TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FetchInvoiceStatus.InvoiceError, result.Fetch.Status);
        Assert.Equal("out of stock", result.Fetch.Error);
        Assert.Equal(86UL, result.Fetch.ErroneousField);
        Assert.Null(result.Payment);
        Assert.Single(_sent);
    }

    [Fact]
    public async Task Given_NoReplyToAnyAttempt_When_Fetching_Then_TimedOutAfterTheAttempts()
    {
        // Arrange
        var offer = _issuer.CreateOffer(chain: Chain);
        for (var i = 0; i < 2; i++)
            _answers.Enqueue(_ => new OnionMessageSendResult(OnionMessageSendStatus.ReplyTimedOut));

        // Act
        var result = await Service().FetchInvoiceAsync(new PayOfferRequest(offer),
                                                       new PayOfferOptions { MaxFetchAttempts = 2 },
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FetchInvoiceStatus.TimedOut, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task Given_AnInvalidInvoiceThenAValidOne_When_Fetching_Then_TheValidOneIsReturned()
    {
        // Arrange: the first invoice asks for another amount (B12-INV-04)
        var offer = _issuer.CreateOffer(chain: Chain);
        _answers.Enqueue(Invoice([PaymentPath(Key(0x10))], amountMsat: 20_000));
        _answers.Enqueue(Invoice([PaymentPath(Key(0x10))]));

        // Act
        var result = await Service().FetchInvoiceAsync(new PayOfferRequest(offer), new PayOfferOptions(),
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Received, result.Status);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task Given_OnlyInvalidInvoices_When_Paying_Then_InvalidInvoiceAndNothingPaid()
    {
        // Arrange
        var offer = _issuer.CreateOffer(chain: Chain);
        for (var i = 0; i < 3; i++)
            _answers.Enqueue(Invoice([PaymentPath(Key(0x10))], amountMsat: 20_000));

        // Act
        var result = await Service().PayOfferAsync(new PayOfferRequest(offer), new PayOfferOptions(),
                                                   TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FetchInvoiceStatus.InvalidInvoice, result.Fetch.Status);
        Assert.Contains("B12-INV-04", result.Fetch.Error);
        Assert.Null(result.Payment);
        Assert.Null(_paid);
    }

    [Fact]
    public async Task Given_OnionMessagesOff_When_Fetching_Then_UnreachableWithoutASend()
    {
        // Arrange
        _onionMessages.SetupGet(s => s.IsAvailable).Returns(false);
        var offer = _issuer.CreateOffer(chain: Chain);

        // Act
        var result = await Service().FetchInvoiceAsync(new PayOfferRequest(offer), new PayOfferOptions(),
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Unreachable, result.Status);
        Assert.Empty(_sent);
        Assert.False(Service().IsAvailable);
    }

    [Fact]
    public async Task Given_NoSigner_When_Fetching_Then_UnreachableAndUnavailable()
    {
        // Arrange
        var service = new OfferPaymentService(_onionMessages.Object, _payments.Object, NodeOptions(),
                                              NullLogger<OfferPaymentService>.Instance, TimeProvider());

        // Act
        var result = await service.FetchInvoiceAsync(new PayOfferRequest(_issuer.CreateOffer(chain: Chain)),
                                                     new PayOfferOptions(), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(service.IsAvailable);
        Assert.Equal(FetchInvoiceStatus.Unreachable, result.Status);
    }

    [Fact]
    public async Task Given_NoPathToTheIssuer_When_Fetching_Then_Unreachable()
    {
        // Arrange
        var offer = _issuer.CreateOffer(chain: Chain);
        for (var i = 0; i < 3; i++)
            _answers.Enqueue(_ => new OnionMessageSendResult(OnionMessageSendStatus.NoPath));

        // Act
        var result = await Service().FetchInvoiceAsync(new PayOfferRequest(offer), new PayOfferOptions(),
                                                       TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FetchInvoiceStatus.Unreachable, result.Status);
        Assert.Equal(0, result.Attempts);
        Assert.Contains("NoPath", result.Error);
    }

    [Fact]
    public async Task Given_AnExpiredOffer_When_Paying_Then_ArgumentExceptionAndNothingSent()
    {
        // Arrange
        var offer = _issuer.CreateOffer(chain: Chain, absoluteExpiry: (ulong)Now.ToUnixTimeSeconds() - 10);

        // Act / Assert
        await Assert.ThrowsAsync<ArgumentException>(() => Service().PayOfferAsync(
                                                        new PayOfferRequest(offer), new PayOfferOptions(),
                                                        TestContext.Current.CancellationToken));
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(OfferPaymentService.MaxFetchAttemptsLimit + 1)]
    public async Task Given_FetchAttemptsOutOfRange_When_Fetching_Then_ArgumentOutOfRange(int attempts)
    {
        // Act / Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service().FetchInvoiceAsync(
                                                                  new PayOfferRequest(_issuer.CreateOffer(chain: Chain)),
                                                                  new PayOfferOptions { MaxFetchAttempts = attempts },
                                                                  TestContext.Current.CancellationToken));
    }

    private OfferPaymentService Service() =>
        new(_onionMessages.Object, _payments.Object, NodeOptions(), NullLogger<OfferPaymentService>.Instance,
            TimeProvider(), _payer);

    private static IOptions<NodeOptions> NodeOptions() =>
        Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest });

    private static TimeProvider TimeProvider()
    {
        var clock = new Mock<TimeProvider>();
        clock.Setup(c => c.GetUtcNow()).Returns(Now);
        return clock.Object;
    }

    private Func<byte[], OnionMessageSendResult> Invoice(IReadOnlyList<Domain.Protocol.Onion.Models.BlindedPaymentPath> paths,
                                                         ulong amountMsat = 10_000, byte[]? features = null) =>
        request => Reply(OnionMessageConstants.InvoiceType,
                         _issuer.CreateInvoice(request, PaymentHash, paths, amountMsat, Now, features: features));

    private static OnionMessageSendResult Reply(ulong type, byte[] value) =>
        new(OnionMessageSendStatus.Replied,
            new ReceivedOnionMessage(OnionMessageContents.Single(type, value), null, new byte[] { 1 }, Key(0x05)));
}