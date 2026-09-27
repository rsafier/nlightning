using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT12;

using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Encoding;
using Domain.Offers.Signing;
using Domain.Offers.Validators;
using Domain.Protocol.Constants;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Offers;

/// <summary>
/// NL-450 (BOLT 12 plan B0-T4): the offer, invoice_request, invoice and invoice_error Core Lightning v26.06.8 sent us,
/// and ours it answered, captured in <see cref="Bolt12ClnVectors"/>: each parses, re-encodes byte for byte, passes its
/// validator, and its BIP-340 signature verifies under the key BOLT 12 names.
/// </summary>
public class Bolt12ClnVectorTests
{
    private static readonly ChainHash[] s_regtest = [ChainConstants.Regtest];
    private static readonly CompactPubKey s_clnNodeId = new(Convert.FromHexString(Bolt12ClnVectors.ClnNodeId));
    private static readonly CompactPubKey s_nltgNodeId = new(Convert.FromHexString(Bolt12ClnVectors.NltgNodeId));

    // Verify never reaches the lightning signer: it only checks a signature
    private static readonly Bolt12Signer s_verifier = new(new Mock<ILightningSigner>(MockBehavior.Strict).Object);

    [Theory]
    [InlineData(nameof(Bolt12ClnVectors.ClnOffer))]
    [InlineData(nameof(Bolt12ClnVectors.NltgOffer))]
    public void Given_ACapturedOffer_When_Parsed_Then_ItReEncodesByteExactAndIsValid(string name)
    {
        // Arrange
        var bytes = Hex(name);

        // Act
        var parsed = Offer.TryParse(bytes, out var offer, out var violation);

        // Assert
        Assert.True(parsed, violation?.ToString());
        Assert.Equal(bytes, offer!.Stream.Encode());
        Assert.Null(OfferValidator.Validate(offer, supportedChains: s_regtest));
        Assert.Equal(bytes, Offer.Parse(offer.ToBolt12String()).Stream.Encode());
    }

    [Fact]
    public void Given_ClnsOffer_When_Parsed_Then_ItCarriesWhatClnWasAskedFor()
    {
        // Act
        var offer = Offer.Parse(Bolt12Bech32.Encode(Bolt12Constants.OfferHrp, Hex(nameof(Bolt12ClnVectors.ClnOffer))));

        // Assert
        Assert.Equal(10_000_000UL, offer.Fields.Amount);
        Assert.Equal("nltg nl-450 capture", offer.Fields.Description);
        Assert.Equal("cln", offer.Fields.Issuer);
        Assert.Equal(s_clnNodeId, offer.Fields.IssuerId);
        Assert.Equal([ChainConstants.Regtest], offer.Fields.Chains!);
    }

    [Theory]
    [InlineData(nameof(Bolt12ClnVectors.ClnInvoiceRequest), nameof(Bolt12ClnVectors.NltgOffer), "cln nl-450")]
    [InlineData(nameof(Bolt12ClnVectors.NltgInvoiceRequest), nameof(Bolt12ClnVectors.ClnOffer), "nl-450")]
    public void Given_ACapturedInvoiceRequest_When_Parsed_Then_ItReEncodesValidatesAndItsPayerSignatureVerifies(
        string name, string offerName, string payerNote)
    {
        // Arrange
        var bytes = Hex(name);

        // Act
        var parsed = InvoiceRequest.TryParse(bytes, out var request, out var violation);

        // Assert
        Assert.True(parsed, violation?.ToString());
        Assert.Equal(bytes, request!.Stream.Encode());
        Assert.Null(InvoiceRequestValidator.Validate(request, s_regtest));
        Assert.Equal(payerNote, request.Fields.PayerNote);
        Assert.True(request.GetOfferStream().ContentEquals(Bolt12TlvStream.Parse(Hex(offerName))),
                    "the offer fields are the offer's bytes");
        Assert.True(s_verifier.Verify(Bolt12Constants.InvoiceRequestSignatureTag, SignedRoot(request.Stream),
                                      request.Fields.PayerId!.Value, request.Signature!.Value));
    }

    [Theory]
    [InlineData(nameof(Bolt12ClnVectors.ClnInvoice), nameof(Bolt12ClnVectors.NltgInvoiceRequest), true)]
    [InlineData(nameof(Bolt12ClnVectors.NltgInvoice), nameof(Bolt12ClnVectors.ClnInvoiceRequest), false)]
    public void Given_ACapturedInvoice_When_Parsed_Then_ItReEncodesValidatesMatchesItsRequestAndItsSignatureVerifies(
        string name, string requestName, bool fromCln)
    {
        // Arrange
        var bytes = Hex(name);
        var request = InvoiceRequest.Parse(Bolt12TlvStream.Parse(Hex(requestName)));
        var expectedNodeId = fromCln ? s_clnNodeId : s_nltgNodeId;

        // Act
        var parsed = Bolt12Invoice.TryParse(Bolt12TlvStream.Parse(bytes), out var invoice, out var violation);

        // Assert
        Assert.True(parsed, violation?.ToString());
        Assert.Equal(bytes, invoice!.Encode());
        var createdAt = DateTimeOffset.FromUnixTimeSeconds((long)invoice.Fields.CreatedAt!.Value);
        Assert.Null(InvoiceValidator.Validate(invoice, createdAt, s_regtest));
        Assert.NotNull(InvoiceValidator.Validate(invoice, createdAt.AddSeconds(invoice.Fields.EffectiveRelativeExpiry + 1),
                                                 s_regtest));
        Assert.Null(InvoiceValidator.ValidateAgainstRequest(invoice, request));
        Assert.Equal(expectedNodeId, invoice.Fields.NodeId);
        Assert.Equal(request.Fields.Amount ?? request.OfferFields.Amount, invoice.Fields.Amount);
        Assert.True(s_verifier.Verify(Bolt12Constants.InvoiceSignatureTag, SignedRoot(invoice.Stream),
                                      expectedNodeId, invoice.Signature!.Value));
    }

    [Fact]
    public void Given_ClnsInvoice_When_TamperedWith_Then_ItsSignatureNoLongerVerifies()
    {
        // Arrange: flip one bit of invoice_amount's value inside the signed records
        var invoice = Bolt12Invoice.Parse(Bolt12TlvStream.Parse(Hex(nameof(Bolt12ClnVectors.ClnInvoice))));
        var records = invoice.Stream.Records
                             .Select(r => r.Type == Bolt12TlvTypes.InvoiceAmount
                                              ? new Bolt12TlvRecord(r.Type, r.Value.ToArray().Select(
                                                                         (b, i) => i == 0 ? (byte)(b ^ 1) : b)
                                                                                  .ToArray())
                                              : r)
                             .ToList();

        // Act
        var verified = s_verifier.Verify(Bolt12Constants.InvoiceSignatureTag,
                                         SignedRoot(new Bolt12TlvStream(records)), s_clnNodeId,
                                         invoice.Signature!.Value);

        // Assert
        Assert.False(verified);
    }

    [Fact]
    public void Given_ClnsInvoiceError_When_Parsed_Then_ItReEncodesAndCarriesClnsReason()
    {
        // Arrange
        var bytes = Hex(nameof(Bolt12ClnVectors.ClnInvoiceError));

        // Act
        var parsed = InvoiceError.TryParse(bytes, out var error, out var violation);

        // Assert
        Assert.True(parsed, violation?.ToString());
        Assert.Equal(bytes, error!.Encode());
        Assert.Contains("Offer no longer available", error.Error);
        Assert.Null(error.ErroneousField);
    }

    private static Hash SignedRoot(Bolt12TlvStream stream) =>
        Bolt12MerkleTree.ComputeRoot(stream.Filter(t => !Bolt12TlvRanges.IsSignatureField(t)));

    private static byte[] Hex(string name) =>
        Convert.FromHexString((string)typeof(Bolt12ClnVectors).GetField(name)!.GetRawConstantValue()!);
}