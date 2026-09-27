namespace NLightning.Domain.Tests.Offers.Validators;

using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Models;
using Domain.Offers.Validators;
using Domain.Protocol.Constants;

public class InvoiceValidatorTests
{
    private static readonly DateTimeOffset s_now = Bolt12TestData.InvoiceCreatedAt.AddMinutes(1);

    [Fact]
    public void Given_ValidInvoice_When_Validating_Then_NoViolation()
    {
        // Arrange
        var invoice = Bolt12Invoice.Parse(Bolt12TestData.InvoiceBuilder().Build());

        // Act & Assert
        Assert.Null(InvoiceValidator.Validate(invoice, s_now, [ChainConstants.Main]));
        Assert.Equal(10_000UL, invoice.Fields.Amount);
        Assert.Equal(Bolt12TestData.IssuerId, invoice.Fields.NodeId);
        Assert.Equal(Bolt12Constants.DefaultInvoiceRelativeExpirySeconds, invoice.Fields.EffectiveRelativeExpiry);
        Assert.Equal(Bolt12TestData.PayerId, invoice.InvoiceRequestFields.PayerId);
        Assert.Equal("coffee", invoice.OfferFields.Description);
        Assert.Equal(64, invoice.Signature!.Value.Length);
    }

    public static TheoryData<string, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder>> InvalidInvoices() => new()
    {
        { "no amount", b => b.Remove(Bolt12TlvTypes.InvoiceAmount) },
        { "no created_at", b => b.Remove(Bolt12TlvTypes.InvoiceCreatedAt) },
        { "no payment_hash", b => b.Remove(Bolt12TlvTypes.InvoicePaymentHash) },
        { "no node_id", b => b.Remove(Bolt12TlvTypes.InvoiceNodeId) },
        { "unsupported chain", b => b.SetChains(Bolt12TlvTypes.InvreqChain, [ChainConstants.Testnet]) },
        { "unknown even feature", b => b.Set(Bolt12TlvTypes.InvoiceFeatures, new byte[] { 0x04, 0x00, 0x00 }) },
        { "expired by default", b => b.SetTu64(Bolt12TlvTypes.InvoiceCreatedAt, 1_699_990_000) },
        { "expired by relative expiry", b => b.SetTu32(Bolt12TlvTypes.InvoiceRelativeExpiry, 59) },
        { "no paths", b => b.Remove(Bolt12TlvTypes.InvoicePaths) },
        { "empty paths", b => b.Set(Bolt12TlvTypes.InvoicePaths, ReadOnlySpan<byte>.Empty) },
        { "no blindedpay", b => b.Remove(Bolt12TlvTypes.InvoiceBlindedPay) },
        {
            "blindedpay count differs",
            b => b.SetPayInfos(Bolt12TlvTypes.InvoiceBlindedPay, [Bolt12TestData.PayInfo(), Bolt12TestData.PayInfo()])
        },
        {
            "no usable path",
            b => b.SetPayInfos(Bolt12TlvTypes.InvoiceBlindedPay, [Bolt12TestData.PayInfo([0x01])])
        },
        { "no signature", b => b.Remove(Bolt12TlvTypes.Signature) },
        { "extra signature element", b => b.Set(999, new byte[] { 1 }) }
    };

    [Theory]
    [MemberData(nameof(InvalidInvoices))]
    public void Given_InvoiceBreakingAReaderRule_When_Validating_Then_ItIsRejected(
        string rule, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder> change)
    {
        // Arrange
        var invoice = Bolt12Invoice.Parse(change(Bolt12TestData.InvoiceBuilder()).Build());

        // Act
        var violation = InvoiceValidator.Validate(invoice, s_now, [ChainConstants.Main]);

        // Assert
        Assert.NotNull(violation);
        Assert.Equal(rule.Contains("signature") ? Bolt12RequirementIds.Signature : Bolt12RequirementIds.InvoiceReader,
                     violation.RequirementId);
    }

    [Fact]
    public void Given_PathWithZeroHops_When_ParsingAnInvoice_Then_ItBreaksTheInvoiceReaderRule()
    {
        // Arrange
        var path = Bolt12TestData.PayerId.ToString() + Bolt12TestData.PathKey + "00";
        var stream = Bolt12TestData.InvoiceBuilder()
                                   .Set(Bolt12TlvTypes.InvoicePaths, Convert.FromHexString(path))
                                   .Build();

        // Act
        var ok = Bolt12Invoice.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.InvoiceReader, violation!.RequirementId);
    }

    [Fact]
    public void Given_OnePathWithUnknownEvenPayInfoFeature_When_Validating_Then_OnlyTheOtherIsUsable()
    {
        // Arrange
        var invoice = Bolt12Invoice.Parse(Bolt12TestData.InvoiceBuilder()
                                                        .SetPaths(Bolt12TlvTypes.InvoicePaths,
                                                                  [Bolt12TestData.Path(), Bolt12TestData.Path()])
                                                        .SetPayInfos(Bolt12TlvTypes.InvoiceBlindedPay,
                                                                     [
                                                                         Bolt12TestData.PayInfo([0x01]),
                                                                         Bolt12TestData.PayInfo([0x02])
                                                                     ])
                                                        .Build());

        // Act & Assert
        Assert.Null(InvoiceValidator.Validate(invoice, s_now));
        Assert.Equal([1], InvoiceValidator.GetUsablePathIndexes(invoice));
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("020000", false, true)]
    [InlineData("010000", true, true)]
    [InlineData("0000", false, false)]
    public void Given_InvoiceFeatures_When_CheckingMpp_Then_TheBitsDecide(string? featuresHex, bool required,
                                                                         bool allowed)
    {
        // Arrange
        var builder = Bolt12TestData.InvoiceBuilder();
        if (featuresHex is not null)
            builder.Set(Bolt12TlvTypes.InvoiceFeatures, Convert.FromHexString(featuresHex));
        var invoice = Bolt12Invoice.Parse(builder.Build());

        // Act & Assert
        Assert.Null(InvoiceValidator.Validate(invoice, s_now));
        Assert.Equal(required, InvoiceValidator.RequiresMultiPart(invoice));
        Assert.Equal(allowed, InvoiceValidator.AllowsMultiPart(invoice));
    }

    [Fact]
    public void Given_Fallbacks_When_Parsed_Then_OnlyValidOnesAreUsable()
    {
        // Arrange
        var invoice = Bolt12Invoice.Parse(Bolt12TestData.InvoiceBuilder()
                                                        .SetFallbacks(Bolt12TlvTypes.InvoiceFallbacks,
                                                                      [
                                                                          new FallbackAddress(0, new byte[20]),
                                                                          new FallbackAddress(17, new byte[20]),
                                                                          new FallbackAddress(1, new byte[1]),
                                                                          new FallbackAddress(1, new byte[41]),
                                                                          new FallbackAddress(16, new byte[40])
                                                                      ])
                                                        .Build());

        // Act
        var usable = invoice.Fields.Fallbacks!.Select(f => f.IsUsable).ToList();

        // Assert
        Assert.Equal([true, false, false, false, true], usable);
        Assert.Null(InvoiceValidator.Validate(invoice, s_now));
    }

    [Theory]
    [InlineData(Bolt12TlvTypes.InvoiceFallbacks, "00")]
    [InlineData(Bolt12TlvTypes.InvoiceFallbacks, "000002aa")]
    [InlineData(Bolt12TlvTypes.InvoiceBlindedPay, "00")]
    [InlineData(Bolt12TlvTypes.InvoiceRelativeExpiry, "0102030405")]
    [InlineData(Bolt12TlvTypes.InvoicePaymentHash, "00")]
    [InlineData(Bolt12TlvTypes.InvoiceNodeId, "030303030303030303030303030303030303030303030303030303030303030303")]
    public void Given_MalformedInvoiceField_When_Parsing_Then_ItIsMalformed(ulong type, string valueHex)
    {
        // Arrange
        var stream = Bolt12TestData.InvoiceBuilder().Set(type, Convert.FromHexString(valueHex)).Build();

        // Act
        var ok = Bolt12Invoice.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.TlvStream, violation!.RequirementId);
    }

    [Theory]
    [InlineData(239UL, true)]
    [InlineData(241UL, true)]
    [InlineData(999UL, true)]
    [InlineData(1_001UL, false)]
    [InlineData(999_999_999UL, false)]
    [InlineData(3_999_999_999UL, true)]
    [InlineData(4_000_000_001UL, false)]
    public void Given_OddType_When_ParsingAnInvoice_Then_OnlyTheInvoiceAndSignatureRangesAreAccepted(ulong type,
        bool accepted)
    {
        // Arrange
        var stream = Bolt12TestData.InvoiceBuilder().Set(type, new byte[] { 1 }).Build();

        // Act
        var ok = Bolt12Invoice.TryParse(stream, out _, out _);

        // Assert
        Assert.Equal(accepted, ok);
    }

    [Fact]
    public void Given_InvoiceForOurRequest_When_Matching_Then_NoViolation()
    {
        // Arrange
        var requestBuilder = Bolt12TestData.InvoiceRequestBuilder().SetTu64(Bolt12TlvTypes.InvreqAmount, 10_000);
        var request = InvoiceRequest.Parse(requestBuilder.Build());
        var invoice = Bolt12Invoice.Parse(Bolt12TestData.InvoiceBuilder(requestBuilder).Build());

        // Act & Assert
        Assert.Null(InvoiceValidator.ValidateAgainstRequest(invoice, request));
        Assert.True(request.Stream.Filter(t => t < 160).ContentEquals(invoice.GetInvoiceRequestStream()));
    }

    public static TheoryData<string, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder>> MismatchedInvoices() => new()
    {
        { "changed description", b => b.SetUtf8(Bolt12TlvTypes.OfferDescription, "tea") },
        { "dropped unknown field", b => b.Remove(1_000_000_001) },
        { "added experimental field", b => b.Set(2_000_000_001, new byte[] { 1 }) },
        { "other node id", b => b.SetPoint(Bolt12TlvTypes.InvoiceNodeId, Bolt12TestData.PayerId) },
        { "other amount", b => b.SetTu64(Bolt12TlvTypes.InvoiceAmount, 10_001) }
    };

    [Theory]
    [MemberData(nameof(MismatchedInvoices))]
    public void Given_InvoiceNotMatchingOurRequest_When_Matching_Then_ItIsInv04(
        string rule, Func<Bolt12TlvStreamBuilder, Bolt12TlvStreamBuilder> change)
    {
        // Arrange
        var requestBuilder = Bolt12TestData.InvoiceRequestBuilder()
                                           .SetTu64(Bolt12TlvTypes.InvreqAmount, 10_000)
                                           .Set(1_000_000_001, new byte[] { 5 });
        var request = InvoiceRequest.Parse(requestBuilder.Build());
        var invoice = Bolt12Invoice.Parse(change(Bolt12TestData.InvoiceBuilder(requestBuilder)).Build());

        // Act
        var violation = InvoiceValidator.ValidateAgainstRequest(invoice, request);

        // Assert
        Assert.NotNull(violation);
        Assert.Equal(Bolt12RequirementIds.InvoiceMatchesRequest, violation.RequirementId);
        Assert.NotNull(rule);
    }

    [Fact]
    public void Given_OfferWithPathsOnly_When_Matching_Then_TheExpectedBlindedNodeIdIsChecked()
    {
        // Arrange
        var offer = Bolt12TestData.OfferBuilder()
                                  .Remove(Bolt12TlvTypes.OfferIssuerId)
                                  .SetPaths(Bolt12TlvTypes.OfferPaths, [Bolt12TestData.Path()]);
        var requestBuilder = Bolt12TestData.InvoiceRequestBuilder(offer);
        var request = InvoiceRequest.Parse(requestBuilder.Build());
        var invoice = Bolt12Invoice.Parse(Bolt12TestData.InvoiceBuilder(requestBuilder)
                                                        .SetPoint(Bolt12TlvTypes.InvoiceNodeId, Bolt12TestData.PathKey)
                                                        .Build());

        // Act & Assert
        Assert.Null(InvoiceValidator.ValidateAgainstRequest(invoice, request, Bolt12TestData.PathKey));
        Assert.NotNull(InvoiceValidator.ValidateAgainstRequest(invoice, request, Bolt12TestData.IssuerId));
    }

    [Fact]
    public void Given_InvoiceBytes_When_ParsedAndEncoded_Then_TheyRoundTrip()
    {
        // Arrange
        var bytes = Bolt12TestData.InvoiceBuilder().Build().Encode();

        // Act
        var invoice = Bolt12Invoice.Parse(bytes);

        // Assert
        Assert.Equal(bytes, invoice.Encode());
        var lni = Domain.Offers.Encoding.Bolt12Bech32.Encode(Bolt12Constants.InvoiceHrp, bytes);
        Assert.True(Bolt12Invoice.TryParse(lni, out var fromString, out _));
        Assert.Equal(bytes, fromString!.Encode());
        Assert.False(Bolt12Invoice.TryParse(Convert.FromHexString("0a").AsMemory(), out _, out _));
    }

    [Fact]
    public void Given_InvoiceWithoutCreatedAt_When_CheckingExpiry_Then_ItIsNotExpired()
    {
        // Arrange
        var invoice = Bolt12Invoice.Parse(Bolt12TestData.InvoiceBuilder().Remove(Bolt12TlvTypes.InvoiceCreatedAt)
                                                        .Build());

        // Act & Assert
        Assert.False(InvoiceValidator.IsExpired(invoice, DateTimeOffset.MaxValue));
        Assert.True(InvoiceValidator.IsExpired(Bolt12Invoice.Parse(Bolt12TestData.InvoiceBuilder().Build()),
                                               Bolt12TestData.InvoiceCreatedAt.AddSeconds(7201)));
        Assert.False(InvoiceValidator.IsExpired(Bolt12Invoice.Parse(Bolt12TestData.InvoiceBuilder().Build()),
                                                Bolt12TestData.InvoiceCreatedAt.AddSeconds(7200)));
    }
}