namespace NLightning.Application.Tests.Offers.Send;

using Application.Offers.Send;
using Domain.Money;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Models;
using Domain.Offers.Signing;
using Domain.Protocol.Constants;
using Domain.Protocol.Tlv;
using static OfferSendTestData;

/// <summary>
/// BOLT 12 "Invoice Requests" writer responding to an offer (B12-IRQ-01).
/// </summary>
public class InvoiceRequestFactoryTests
{
    private readonly TestOfferIssuer _issuer = new();
    private readonly TestBolt12Signer _payer = new(Enumerable.Repeat((byte)0x42, 32).ToArray());

    [Fact]
    public void Given_AnOffer_When_Creating_Then_EveryOfferRecordIsCopiedAndTheSignatureVerifies()
    {
        // Arrange
        var offer = Parse(_issuer.CreateOffer(chain: Chain, extra: [new Bolt12TlvRecord(1_000_000_001, new byte[] { 0xEE })]));

        // Act
        var built = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text, PayerNote: "thanks"), Chain,
                                                 _payer);

        // Assert
        foreach (var record in offer.Stream.Records)
        {
            Assert.True(built.Stream.TryGetValue(record.Type, out var copied));
            Assert.Equal(record.Value.ToArray(), copied.ToArray());
        }

        Assert.True(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqMetadata, out var metadata));
        Assert.Equal(Bolt12Constants.OurInvoiceRequestMetadataLength, metadata.Length);
        Assert.Equal(built.Metadata, metadata.ToArray());
        Assert.True(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqPayerId, out var payerId));
        Assert.Equal((byte[])_payer.DerivePayerId(metadata), payerId.ToArray());
        Assert.True(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqChain, out var chain));
        Assert.Equal((byte[])Chain, chain.ToArray());
        Assert.False(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqAmount, out _));
        Assert.False(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqQuantity, out _));
        Assert.True(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqPayerNote, out var note));
        Assert.Equal("thanks"u8.ToArray(), note.ToArray());
        Assert.True(built.Stream.TryGetValue(Bolt12TlvTypes.Signature, out var signature));
        Assert.True(_payer.Verify(Bolt12Constants.InvoiceRequestSignatureTag, Bolt12MerkleTree.ComputeRoot(built.Stream),
                                  _payer.DerivePayerId(metadata), signature));
        Assert.Equal(built.Bytes, Bolt12TlvStream.Parse(built.Bytes).Encode());
        Assert.Equal(10_000UL, built.ExpectedAmountMsat);
    }

    [Fact]
    public void Given_TwoRequests_When_Creating_Then_MetadataAndPayerIdsDiffer()
    {
        // Arrange
        var offer = Parse(_issuer.CreateOffer(chain: Chain));

        // Act
        var first = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text), Chain, _payer);
        var second = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text), Chain, _payer);

        // Assert
        Assert.NotEqual(first.Metadata, second.Metadata);
        Assert.NotEqual(first.PayerId, second.PayerId);
    }

    [Fact]
    public void Given_ARequest_When_ItsMetadataIsChecked_Then_ItCommitsToExactlyItsOtherFields()
    {
        // Arrange (NL-1157): the fields an invoice mirrors, without the metadata and the payer id
        var offer = Parse(_issuer.CreateOffer(chain: Chain));
        var built = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text, PayerNote: "note"), Chain,
                                                 _payer);
        var fields = built.Stream.Records.Where(r => InvoiceVerifier.IsMirroredType(r.Type)
                                                  && r.Type is not (Bolt12TlvTypes.InvreqMetadata
                                                                    or Bolt12TlvTypes.InvreqPayerId)).ToList();
        var changedNote = fields.Select(r => r.Type == Bolt12TlvTypes.InvreqPayerNote
                                                 ? new Bolt12TlvRecord(r.Type, "other"u8.ToArray())
                                                 : r).ToList();
        var addedAmount = fields.Append(new Bolt12TlvRecord(Bolt12TlvTypes.InvreqAmount, new byte[] { 0x01 })).ToList();
        var otherNonce = built.Metadata.ToArray();
        otherNonce[0] ^= 0x01;

        // Act / Assert: the request's own fields in any order pass; a changed, added or dropped field, another nonce or
        // random metadata (requests made before NL-1157) do not
        Assert.True(InvoiceRequestFactory.IsCommittedMetadata(built.Metadata, fields));
        Assert.True(InvoiceRequestFactory.IsCommittedMetadata(built.Metadata, Enumerable.Reverse(fields)));
        Assert.False(InvoiceRequestFactory.IsCommittedMetadata(built.Metadata, changedNote));
        Assert.False(InvoiceRequestFactory.IsCommittedMetadata(built.Metadata, addedAmount));
        Assert.False(InvoiceRequestFactory.IsCommittedMetadata(built.Metadata, fields.Skip(1)));
        Assert.False(InvoiceRequestFactory.IsCommittedMetadata(otherNonce, fields));
        Assert.False(InvoiceRequestFactory.IsCommittedMetadata(new byte[32], fields));
        Assert.False(InvoiceRequestFactory.IsCommittedMetadata(built.Metadata.AsSpan(0, 16), fields));
    }

    [Fact]
    public void Given_AMainnetOfferWithoutChains_When_Creating_Then_NoInvreqChain()
    {
        // Arrange
        var offer = OfferToPay.Parse(_issuer.CreateOffer(), ChainConstants.Main, Now);

        // Act
        var built = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text), ChainConstants.Main, _payer);

        // Assert
        Assert.False(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqChain, out _));
    }

    [Fact]
    public void Given_AnAmountlessOffer_When_CreatingWithAnAmount_Then_InvreqAmountIsSet()
    {
        // Arrange
        var offer = Parse(_issuer.CreateOffer(chain: Chain, amountMsat: null, description: null));

        // Act
        var built = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text, LightningMoney.MilliSatoshis(1234)),
                                                 Chain, _payer);

        // Assert
        Assert.True(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqAmount, out var amount));
        Assert.Equal(1234UL, TruncatedInt.DecodeTu64(amount.Span));
        Assert.True(built.HasAmount);
        Assert.Equal(1234UL, built.ExpectedAmountMsat);
    }

    [Fact]
    public void Given_AQuantityOffer_When_CreatingWithAQuantity_Then_TheExpectedAmountIsMultiplied()
    {
        // Arrange
        var offer = Parse(_issuer.CreateOffer(chain: Chain, quantityMax: 5));

        // Act
        var built = InvoiceRequestFactory.Create(offer, new PayOfferRequest(offer.Text, Quantity: 3), Chain, _payer);

        // Assert
        Assert.True(built.Stream.TryGetValue(Bolt12TlvTypes.InvreqQuantity, out var quantity));
        Assert.Equal(3UL, TruncatedInt.DecodeTu64(quantity.Span));
        Assert.Equal(30_000UL, built.ExpectedAmountMsat);
    }

    [Theory]
    [InlineData("amountless without amount")]
    [InlineData("currency without amount")]
    [InlineData("amount below the offer")]
    [InlineData("zero amount")]
    [InlineData("quantity without max")]
    [InlineData("max without quantity")]
    [InlineData("zero quantity")]
    [InlineData("quantity above max")]
    [InlineData("empty note")]
    public void Given_ARequestBreakingAWriterRule_When_Creating_Then_ArgumentExceptionAndNothingSigned(string @case)
    {
        // Arrange
        var (offerText, request) = @case switch
        {
            "amountless without amount" => (_issuer.CreateOffer(chain: Chain, amountMsat: null), new PayOfferRequest("")),
            "currency without amount" => (_issuer.CreateOffer(chain: Chain, currency: "USD"), new PayOfferRequest("")),
            "amount below the offer" => (_issuer.CreateOffer(chain: Chain),
                                         new PayOfferRequest("", LightningMoney.MilliSatoshis(9_999))),
            "zero amount" => (_issuer.CreateOffer(chain: Chain), new PayOfferRequest("", LightningMoney.Zero)),
            "quantity without max" => (_issuer.CreateOffer(chain: Chain), new PayOfferRequest("", Quantity: 1)),
            "max without quantity" => (_issuer.CreateOffer(chain: Chain, quantityMax: 0), new PayOfferRequest("")),
            "zero quantity" => (_issuer.CreateOffer(chain: Chain, quantityMax: 0), new PayOfferRequest("", Quantity: 0)),
            "quantity above max" => (_issuer.CreateOffer(chain: Chain, quantityMax: 2), new PayOfferRequest("", Quantity: 3)),
            _ => (_issuer.CreateOffer(chain: Chain), new PayOfferRequest("", PayerNote: ""))
        };
        var offer = Parse(offerText);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => InvoiceRequestFactory.Create(offer, request with { Offer = offerText },
                                                                            Chain, _payer));
        Assert.Equal(0, _payer.PayerSignatures);
    }

    private static OfferToPay Parse(string text) => OfferToPay.Parse(text, Chain, Now);
}