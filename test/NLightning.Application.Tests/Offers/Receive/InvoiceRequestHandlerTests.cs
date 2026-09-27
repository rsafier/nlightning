using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Offers.Receive;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Enums;
using Domain.Offers.Models;
using Domain.Offers.Signing;
using Domain.Payments.Enums;
using Domain.Protocol.Constants;
using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Interfaces;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Payments;

/// <summary>
/// Plan B3-T2/B3-T3: the BOLT 12 "Invoice Requests" reader rules for our offers (B12-IRQ-02..05), the invoice we
/// answer with (B12-INV-01/02: copied fields, paths and payinfo, amount, node id, one signature), the
/// <c>invoice_error</c> only after a valid signature (plan D10), the caps (D11) and the invoice row saved before the
/// reply.
/// </summary>
public sealed class InvoiceRequestHandlerTests : IDisposable
{
    private static readonly byte[] s_nodeKey = Enumerable.Repeat((byte)0x07, 32).ToArray();
    private static readonly DateTimeOffset s_now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private readonly OfferTestStore _store = new();
    private readonly RecordingOnionMessageService _onionMessages = new();
    private readonly TestNodeKeyManager _keyManager = new(0x07);
    private readonly TestBolt12Signer _signer = new(s_nodeKey);
    private readonly Mock<IBlindedPaymentPathSource> _paths = new();
    private readonly ManualClock _clock = new(s_now);
    private readonly NodeOptions _nodeOptions = new() { BitcoinNetwork = BitcoinNetwork.Regtest };
    private readonly OfferOptions _offerOptions = new();
    private readonly ServiceProvider _services;
    private readonly OfferPathIds _pathIds;
    private readonly WireBlindedPath _replyPath = TestPaths.ReplyPath();

    public InvoiceRequestHandlerTests()
    {
        _services = new ServiceCollection().AddSingleton<IOnionMessageService>(_onionMessages).BuildServiceProvider();
        _pathIds = new OfferPathIds(_keyManager);
        _paths.Setup(p => p.CreateAsync(It.IsAny<Secret>(), It.IsAny<LightningMoney>(), It.IsAny<uint>(),
                                        It.IsAny<CancellationToken>()))
              .ReturnsAsync([TestPaths.PaymentPath(0x21), TestPaths.PaymentPath(0x31)]);
    }

    public void Dispose()
    {
        _services.Dispose();
        _store.Dispose();
    }

    private InvoiceRequestHandler CreateHandler(InvoiceRequestRateLimiter? limiter = null) =>
        new(_store.ScopeFactory, _services, _keyManager, _signer, _paths.Object, _pathIds,
            Options.Create(_nodeOptions), limiter ?? new InvoiceRequestRateLimiter(5, 20, _clock),
            NullLogger<InvoiceRequestHandler>.Instance, Options.Create(_offerOptions), _clock);

    private async Task<OfferModel> AddOfferAsync(ulong? amountMsat = 50_000, bool withPaths = false,
                                                 ulong? quantityMax = null, DateTimeOffset? expiry = null)
    {
        var metadata = RandomNumberGenerator.GetBytes(16);
        var request = new CreateOfferRequest(amountMsat is { } a ? LightningMoney.MilliSatoshis(a) : null, "coffee",
                                             QuantityMax: quantityMax, AbsoluteExpiry: expiry);
        IReadOnlyList<WireBlindedPath> paths = withPaths ? [TestPaths.ReplyPath()] : [];
        var bytes = new Bolt12TlvStream(OfferService.BuildRecords(request, BitcoinNetwork.Regtest, metadata, paths,
                                                                  _keyManager.NodeId)).Encode();
        var offer = new OfferModel(new Hash(SHA256.HashData(bytes)), Bolt12Bech32.Encode("lno", bytes), bytes,
                                   request.Description, request.Amount, null, null, quantityMax, expiry, metadata,
                                   OfferIssuerKind.NodeId, withPaths, s_now);
        await _store.Offers.AddAsync(offer);
        return offer;
    }

    private static InvoiceRequestBuilder RequestFor(OfferModel offer) =>
        new InvoiceRequestBuilder(offer.OfferBytes).Chain((byte[])ChainConstants.Regtest);

    private ReceivedOnionMessage Message(byte[] invoiceRequest, ReadOnlyMemory<byte>? pathId = null,
                                         bool withReplyPath = true)
    {
        var message = new ReceivedOnionMessage(
            OnionMessageContents.Single(InvoiceRequestHandler.InvoiceRequestType, invoiceRequest),
            withReplyPath ? _replyPath : null, null, TestPaths.Point(0x55));
        return pathId is { } id ? message with { PathId = id } : message;
    }

    private Task<InvoiceRequestOutcome> ProcessAsync(byte[] invoiceRequest, ReadOnlyMemory<byte>? pathId = null,
                                                     InvoiceRequestHandler? handler = null) =>
        (handler ?? CreateHandler()).ProcessAsync(Message(invoiceRequest, pathId),
                                                  TestContext.Current.CancellationToken);

    private (ulong Type, Bolt12TlvStreamView Stream) SingleReply()
    {
        var (destination, contents) = Assert.Single(_onionMessages.Sent);
        Assert.Same(_replyPath, destination.BlindedPath);
        var record = Assert.Single(contents.Records);
        Assert.True(Bolt12TlvStream.TryParse(record.Value, out var stream));
        return (record.Type, new Bolt12TlvStreamView(stream!));
    }

    [Fact]
    public async Task Given_AValidRequest_When_Processed_Then_ASignedInvoiceIsSentThroughTheReplyPath()
    {
        // Arrange
        var offer = await AddOfferAsync();
        var builder = RequestFor(offer).PayerNote("thanks").Set(2_000_000_001, new byte[] { 9 });
        var request = builder.Build();

        // Act
        var outcome = await ProcessAsync(request);

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Invoice, outcome);
        var (type, invoice) = SingleReply();
        Assert.Equal(InvoiceRequestHandler.InvoiceType, type);
        // B12-INV-01: every non-signature field of the request copied, unknown odd ones included
        foreach (var record in builder.Records)
            Assert.Equal(record.Value.ToArray(), invoice.Get(record.Type));
        Assert.Equal(50_000UL, TruncatedInt.DecodeTu64(invoice.Get(Bolt12TlvTypes.InvoiceAmount)));
        Assert.Equal((ulong)s_now.ToUnixTimeSeconds(),
                     TruncatedInt.DecodeTu64(invoice.Get(Bolt12TlvTypes.InvoiceCreatedAt)));
        Assert.False(invoice.Has(Bolt12TlvTypes.InvoiceRelativeExpiry));
        Assert.Equal((byte[])_keyManager.NodeId, invoice.Get(Bolt12TlvTypes.InvoiceNodeId));
        // B12-INV-02: the paths and one blinded_payinfo per path, in order, with empty features
        var paths = invoice.Get(Bolt12TlvTypes.InvoicePaths);
        Assert.True(BlindedPathCodec.TryReadList(paths, out var pathList, out _));
        Assert.Equal(2, pathList.Count);
        Assert.Equal(TestPaths.PaymentPath(0x21).Path.FirstNodeId, pathList[0].FirstNode.NodeId);
        Assert.Equal([.. Bolt12FieldCodec.EncodePayInfos([TestPaths.PaymentPath(0x21).PayInfo]),
                      .. Bolt12FieldCodec.EncodePayInfos([TestPaths.PaymentPath(0x31).PayInfo])],
                     invoice.Get(Bolt12TlvTypes.InvoiceBlindedPay));
        // basic_mpp is on by default: MPP optional (bit 17)
        Assert.Equal(new byte[] { 0x02, 0x00, 0x00 }, invoice.Get(Bolt12TlvTypes.InvoiceFeatures));
        // Exactly one signature, by invoice_node_id over the Merkle root
        Assert.Single(invoice.Stream.Records, r => Bolt12TlvRanges.IsSignatureField(r.Type));
        Assert.True(Bip340.Verify(Bolt12Constants.InvoiceSignatureTag,
                                  Bolt12MerkleTree.ComputeRoot(invoice.Stream), _keyManager.NodeId,
                                  invoice.Get(Bolt12TlvTypes.Signature)));
    }

    [Fact]
    public async Task Given_AValidRequest_When_Processed_Then_TheInvoiceRowIsSavedBeforeTheReply()
    {
        // Arrange
        var offer = await AddOfferAsync();
        var savesAtSend = -1;
        _onionMessages.OnSend = () => savesAtSend = _store.Saves;

        // Act
        await ProcessAsync(RequestFor(offer).PayerNote("n").Build());

        // Assert
        Assert.Equal(1, savesAtSend);
        var row = Assert.Single(_store.Invoices.Invoices);
        Assert.Equal(InvoiceKind.Bolt12, row.Kind);
        Assert.Equal(offer.OfferId, row.Bolt12!.OfferId);
        Assert.Equal(Bip340.PublicKey(InvoiceRequestBuilder.PayerKey), row.Bolt12.PayerId);
        Assert.Equal("n", row.Bolt12.PayerNote);
        Assert.Equal(InvoiceStatus.Open, row.Status);
        Assert.Equal(LightningMoney.MilliSatoshis(50_000), row.Amount);
        Assert.Equal(new Hash(SHA256.HashData(row.Preimage)), row.PaymentHash);
        // B12-0's InvoiceModel still requires a string (the invoice as lni1...); B12-C's takes none
        Assert.True(row.Bolt11 is null || row.Bolt11.StartsWith("lni1", StringComparison.Ordinal));
        var (_, invoice) = SingleReply();
        Assert.Equal(row.Bolt12.InvoiceBytes.ToArray(), Assert.Single(_onionMessages.Sent).Contents.Records[0].Value
                                                              .ToArray());
        Assert.Equal((byte[])row.PaymentHash, invoice.Get(Bolt12TlvTypes.InvoicePaymentHash));
        _paths.Verify(p => p.CreateAsync(row.Preimage, row.Amount!, Bolt12Constants.DefaultInvoiceRelativeExpirySeconds,
                                         It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_ABadSignature_When_Processed_Then_NothingIsAnswered()
    {
        // Arrange
        var offer = await AddOfferAsync();
        var otherKey = Enumerable.Repeat((byte)0x43, 32).ToArray();

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Build(otherKey));

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Ignored, outcome);
        Assert.Empty(_onionMessages.Sent);
        Assert.Empty(_store.Invoices.Invoices);
    }

    [Theory]
    [InlineData("no metadata")]
    [InlineData("no payer id")]
    [InlineData("out of range")]
    [InlineData("unknown even feature")]
    public async Task Given_AMalformedRequest_When_Processed_Then_NothingIsAnswered(string variant)
    {
        // Arrange
        var offer = await AddOfferAsync();
        var builder = RequestFor(offer);
        _ = variant switch
        {
            "no metadata" => builder.Remove(Bolt12TlvTypes.InvreqMetadata),
            "no payer id" => builder.Remove(Bolt12TlvTypes.InvreqPayerId),
            "out of range" => builder.Set(161, new byte[] { 1 }),
            _ => builder.Set(Bolt12TlvTypes.InvreqFeatures, new byte[] { 0x01 })
        };

        // Act
        var outcome = await ProcessAsync(builder.Build());

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Ignored, outcome);
        Assert.Empty(_onionMessages.Sent);
    }

    [Fact]
    public async Task Given_NoReplyPath_When_Processed_Then_Ignored()
    {
        // Arrange
        var offer = await AddOfferAsync();

        // Act
        var outcome = await CreateHandler().ProcessAsync(Message(RequestFor(offer).Build(), withReplyPath: false),
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Ignored, outcome);
        Assert.Empty(_onionMessages.Sent);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("through another offer's path")]
    public async Task Given_AnUnknownOffer_When_Processed_Then_IgnoredWithoutAnyReply(string variant)
    {
        // Arrange: BOLT 12 rationale: an answer for an offer that is not ours would tell a prober, by the silence it
        // gets for ours, which offers belong to this node (an offer's paths linked to our node id, or two offers)
        var ours = await AddOfferAsync(withPaths: true);
        var request = RequestFor(ours).Set(Bolt12TlvTypes.OfferDescription, "tea"u8.ToArray()).Build();
        ReadOnlyMemory<byte>? pathId = variant == "direct" ? null : _pathIds.Compute(ours.Metadata.Span);

        // Act
        var outcome = await ProcessAsync(request, pathId);

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Ignored, outcome);
        Assert.Empty(_onionMessages.Sent);
        Assert.Empty(_store.Invoices.Invoices);
    }

    [Fact]
    public async Task Given_ABurstOfBadSignaturesAndUnknownOffers_When_OverTheNodeRate_Then_LaterRequestsAreDropped()
    {
        // Arrange: node-wide 20 per second; every request costs a token before its signature is checked
        var offer = await AddOfferAsync();
        var handler = CreateHandler();
        var badSignature = RequestFor(offer).Build(Enumerable.Repeat((byte)0x43, 32).ToArray());
        var unknownOffer = RequestFor(offer).Set(Bolt12TlvTypes.OfferDescription, "tea"u8.ToArray()).Build();
        for (var i = 0; i < 20; i++)
            Assert.Equal(InvoiceRequestOutcome.Ignored,
                         await ProcessAsync(i % 2 == 0 ? badSignature : unknownOffer, handler: handler));

        // Act
        var dropped = await ProcessAsync(RequestFor(offer).Build(), handler: handler);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var answered = await ProcessAsync(RequestFor(offer).Build(), handler: handler);

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Ignored, dropped);
        Assert.Equal(InvoiceRequestOutcome.Invoice, answered);
        Assert.Single(_onionMessages.Sent);
    }

    [Fact]
    public async Task Given_AnOfferWithPaths_When_TheRequestComesThroughOurPath_Then_Answered()
    {
        // Arrange
        var offer = await AddOfferAsync(withPaths: true);

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Build(), _pathIds.Compute(offer.Metadata.Span));

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Invoice, outcome);
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("another path_id")]
    public async Task Given_AnOfferWithPaths_When_TheRequestComesOtherwise_Then_Ignored(string variant)
    {
        // Arrange
        var offer = await AddOfferAsync(withPaths: true);
        ReadOnlyMemory<byte>? pathId = variant == "direct" ? null : _pathIds.Compute(new byte[16]);

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Build(), pathId);

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Ignored, outcome);
        Assert.Empty(_onionMessages.Sent);
    }

    [Fact]
    public async Task Given_AnOfferWithoutPaths_When_TheRequestComesThroughABlindedPathOfOurs_Then_Ignored()
    {
        // Arrange
        var offer = await AddOfferAsync();

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Build(), new byte[32]);

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Ignored, outcome);
        Assert.Empty(_onionMessages.Sent);
    }

    [Fact]
    public async Task Given_AnAmountBelowTheOffer_When_Processed_Then_InvoiceErrorNamesInvreqAmount()
    {
        // Arrange
        var offer = await AddOfferAsync(amountMsat: 50_000);

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Amount(49_999).Build());

        // Assert
        Assert.Equal(InvoiceRequestOutcome.InvoiceError, outcome);
        var (_, error) = SingleReply();
        Assert.Equal(Bolt12TlvTypes.InvreqAmount, TruncatedInt.DecodeTu64(error.Get(Bolt12TlvTypes.ErroneousField)));
    }

    [Fact]
    public async Task Given_ATipAboveTheOffer_When_Processed_Then_TheInvoiceIsForTheRequestedAmount()
    {
        // Arrange
        var offer = await AddOfferAsync(amountMsat: 50_000);

        // Act
        await ProcessAsync(RequestFor(offer).Amount(70_000).Build());

        // Assert
        var (_, invoice) = SingleReply();
        Assert.Equal(70_000UL, TruncatedInt.DecodeTu64(invoice.Get(Bolt12TlvTypes.InvoiceAmount)));
    }

    [Fact]
    public async Task Given_AnAmountlessOffer_When_TheRequestNamesAnAmount_Then_TheInvoiceIsForIt()
    {
        // Arrange
        var offer = await AddOfferAsync(amountMsat: null);

        // Act
        var withAmount = await ProcessAsync(RequestFor(offer).Amount(12_345).Build());
        var withoutAmount = await ProcessAsync(RequestFor(offer).Build());

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Invoice, withAmount);
        Assert.Equal(InvoiceRequestOutcome.InvoiceError, withoutAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(12_345), Assert.Single(_store.Invoices.Invoices).Amount);
    }

    [Fact]
    public async Task Given_AQuantity_When_WithinTheOfferMaximum_Then_TheAmountIsMultiplied()
    {
        // Arrange
        var offer = await AddOfferAsync(amountMsat: 1_000, quantityMax: 5);

        // Act
        await ProcessAsync(RequestFor(offer).Quantity(3).Build());

        // Assert
        var (_, invoice) = SingleReply();
        Assert.Equal(3_000UL, TruncatedInt.DecodeTu64(invoice.Get(Bolt12TlvTypes.InvoiceAmount)));
        Assert.Equal(3UL, Assert.Single(_store.Invoices.Invoices).Bolt12!.Quantity);
    }

    [Theory]
    [InlineData(6UL)]
    [InlineData(0UL)]
    public async Task Given_AQuantityOutOfRange_When_Processed_Then_InvoiceErrorNamesInvreqQuantity(ulong quantity)
    {
        // Arrange
        var offer = await AddOfferAsync(amountMsat: 1_000, quantityMax: 5);

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Quantity(quantity).Build());

        // Assert
        Assert.Equal(InvoiceRequestOutcome.InvoiceError, outcome);
        var (_, error) = SingleReply();
        Assert.Equal(Bolt12TlvTypes.InvreqQuantity, TruncatedInt.DecodeTu64(error.Get(Bolt12TlvTypes.ErroneousField)));
    }

    [Fact]
    public async Task Given_AnotherChain_When_Processed_Then_InvoiceErrorNamesInvreqChain()
    {
        // Arrange
        var offer = await AddOfferAsync();

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Remove(Bolt12TlvTypes.InvreqChain).Build());

        // Assert
        Assert.Equal(InvoiceRequestOutcome.InvoiceError, outcome);
        var (_, error) = SingleReply();
        Assert.Equal(Bolt12TlvTypes.InvreqChain, TruncatedInt.DecodeTu64(error.Get(Bolt12TlvTypes.ErroneousField)));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("expired")]
    public async Task Given_AnOfferThatNoLongerAnswers_When_Processed_Then_InvoiceError(string variant)
    {
        // Arrange
        var offer = await AddOfferAsync(expiry: s_now.AddHours(1));
        if (variant == "disabled")
            offer.Disable(s_now);
        else
            _clock.Advance(TimeSpan.FromHours(2));

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Build());

        // Assert
        Assert.Equal(InvoiceRequestOutcome.InvoiceError, outcome);
        Assert.Empty(_store.Invoices.Invoices);
    }

    [Fact]
    public async Task Given_TheUnpaidCapReached_When_Processed_Then_InvoiceErrorAndNoRow()
    {
        // Arrange
        var offer = await AddOfferAsync();
        _offerOptions.MaxUnpaidInvoicesPerOffer = 2;
        await ProcessAsync(RequestFor(offer).Build());
        await ProcessAsync(RequestFor(offer).Build());
        _onionMessages.Sent.Clear();

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Build());

        // Assert
        Assert.Equal(InvoiceRequestOutcome.InvoiceError, outcome);
        var (_, error) = SingleReply();
        Assert.Equal("Temporarily unavailable", Encoding.UTF8.GetString(error.Get(Bolt12TlvTypes.Error)));
        Assert.Equal(2, _store.Invoices.Invoices.Count);
    }

    [Fact]
    public async Task Given_NoPaymentPath_When_Processed_Then_InvoiceErrorAndNoRow()
    {
        // Arrange
        var offer = await AddOfferAsync();
        _paths.Setup(p => p.CreateAsync(It.IsAny<Secret>(), It.IsAny<LightningMoney>(), It.IsAny<uint>(),
                                        It.IsAny<CancellationToken>()))
              .ReturnsAsync([]);

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Build());

        // Assert
        Assert.Equal(InvoiceRequestOutcome.InvoiceError, outcome);
        Assert.Empty(_store.Invoices.Invoices);
    }

    [Fact]
    public async Task Given_ABurstForOneOffer_When_OverFivePerSecond_Then_TheRestIsDropped()
    {
        // Arrange
        var offer = await AddOfferAsync();
        var handler = CreateHandler();
        var outcomes = new List<InvoiceRequestOutcome>();

        // Act
        for (var i = 0; i < 7; i++)
            outcomes.Add(await ProcessAsync(RequestFor(offer).Build(), handler: handler));
        _clock.Advance(TimeSpan.FromSeconds(1));
        outcomes.Add(await ProcessAsync(RequestFor(offer).Build(), handler: handler));

        // Assert
        Assert.Equal([.. Enumerable.Repeat(InvoiceRequestOutcome.Invoice, 5), InvoiceRequestOutcome.Ignored,
                      InvoiceRequestOutcome.Ignored, InvoiceRequestOutcome.Invoice], outcomes);
    }

    [Fact]
    public async Task Given_BasicMppOffAndAnotherExpiry_When_Processed_Then_NoFeaturesAndTheExpiryIsWritten()
    {
        // Arrange
        var offer = await AddOfferAsync();
        _nodeOptions.Features.BasicMpp = FeatureSupport.No;
        _offerOptions.InvoiceRelativeExpirySeconds = 3_600;

        // Act
        await ProcessAsync(RequestFor(offer).Build());

        // Assert
        var (_, invoice) = SingleReply();
        Assert.False(invoice.Has(Bolt12TlvTypes.InvoiceFeatures));
        Assert.Equal(3_600U, TruncatedInt.DecodeTu32(invoice.Get(Bolt12TlvTypes.InvoiceRelativeExpiry)));
        Assert.Equal(3_600U, Assert.Single(_store.Invoices.Invoices).ExpirySeconds);
    }

    [Fact]
    public async Task Given_NoSigner_When_Processed_Then_Ignored()
    {
        // Arrange
        var offer = await AddOfferAsync();
        var handler = new InvoiceRequestHandler(_store.ScopeFactory, _services, _keyManager, null, _paths.Object,
                                                _pathIds, Options.Create(_nodeOptions),
                                                new InvoiceRequestRateLimiter(5, 20, _clock),
                                                NullLogger<InvoiceRequestHandler>.Instance);

        // Act
        var outcome = await ProcessAsync(RequestFor(offer).Build(), handler: handler);

        // Assert
        Assert.Equal(InvoiceRequestOutcome.Ignored, outcome);
        Assert.Empty(_onionMessages.Sent);
    }

    [Fact]
    public void Given_TheHandler_When_Asked_Then_ItClaimsInvoiceRequestsOnly()
    {
        // Act
        var types = CreateHandler().PayloadTypes;

        // Assert
        Assert.Equal([64UL], types);
    }
}

/// <summary>
/// Reads fields of a parsed TLV stream in tests.
/// </summary>
internal sealed class Bolt12TlvStreamView(Domain.Offers.Bolt12TlvStream stream)
{
    public Domain.Offers.Bolt12TlvStream Stream { get; } = stream;

    public bool Has(ulong type) => Stream.TryGetValue(type, out _);

    public byte[] Get(ulong type) =>
        Stream.TryGetValue(type, out var value) ? value.ToArray() : throw new KeyNotFoundException($"type {type}");
}