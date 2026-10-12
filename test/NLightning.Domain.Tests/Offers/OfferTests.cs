namespace NLightning.Domain.Tests.Offers;

using Domain.Offers;
using Domain.Offers.Constants;
using Domain.Offers.Validators;
using Domain.Protocol.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;

public class OfferTests
{
    [Fact]
    public void Given_BuiltOffer_When_EncodedAndParsed_Then_EveryFieldRoundTrips()
    {
        // Arrange
        var stream = new Bolt12TlvStreamBuilder()
                    .SetChains(Bolt12TlvTypes.OfferChains, [ChainConstants.Regtest, ChainConstants.Main])
                    .Set(Bolt12TlvTypes.OfferMetadata, new byte[] { 9, 9 })
                    .SetUtf8(Bolt12TlvTypes.OfferCurrency, "USD")
                    .SetTu64(Bolt12TlvTypes.OfferAmount, 1234)
                    .SetUtf8(Bolt12TlvTypes.OfferDescription, "café ☕")
                    .Set(Bolt12TlvTypes.OfferFeatures, new byte[] { 0x02 })
                    .SetTu64(Bolt12TlvTypes.OfferAbsoluteExpiry, 2_000_000_000)
                    .SetPaths(Bolt12TlvTypes.OfferPaths, [Bolt12TestData.Path()])
                    .SetUtf8(Bolt12TlvTypes.OfferIssuer, "nltg")
                    .SetTu64(Bolt12TlvTypes.OfferQuantityMax, 0)
                    .SetPoint(Bolt12TlvTypes.OfferIssuerId, Bolt12TestData.IssuerId)
                    .Set(1_000_000_001, new byte[] { 7 })
                    .Build();

        // Act
        var text = Offer.Parse(stream).ToBolt12String();
        var offer = Offer.Parse(text);

        // Assert
        var fields = offer.Fields;
        Assert.StartsWith("lno1", text);
        Assert.Equal([ChainConstants.Regtest, ChainConstants.Main], fields.Chains);
        Assert.Equal([9, 9], fields.Metadata!.Value.ToArray());
        Assert.Equal("USD", fields.Currency);
        Assert.Equal(1234UL, fields.Amount);
        Assert.Equal("café ☕", fields.Description);
        Assert.Equal([0x02], fields.Features!.Value.ToArray());
        Assert.Equal(2_000_000_000UL, fields.AbsoluteExpiry);
        Assert.Equal(Bolt12TestData.Path().FirstPathKey, Assert.Single(fields.Paths!).FirstPathKey);
        Assert.Equal("nltg", fields.Issuer);
        Assert.Equal(0UL, fields.QuantityMax);
        Assert.Equal(Bolt12TestData.IssuerId, fields.IssuerId);
        Assert.True(fields.IsOfferResponse);
        Assert.True(offer.Stream.TryGetValue(1_000_000_001, out _));
        Assert.Equal(stream.Encode(), offer.Stream.Encode());
        Assert.Equal(text, offer.ToString());
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(80UL)]
    [InlineData(81UL)]
    [InlineData(999_999_999UL)]
    [InlineData(2_000_000_000UL)]
    [InlineData(2_000_000_001UL)]
    [InlineData(ulong.MaxValue)]
    public void Given_TypeOutsideTheOfferRanges_When_Parsing_Then_ItIsARangeViolation(ulong type)
    {
        // Arrange
        var stream = Bolt12TestData.OfferBuilder().Set(type, new byte[] { 1 }).Build();

        // Act
        var ok = Offer.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.TlvRange, violation!.RequirementId);
        Assert.Equal(type, violation.Field);
    }

    [Theory]
    [InlineData(24UL)]
    [InlineData(78UL)]
    [InlineData(1_000_000_002UL)]
    [InlineData(1_999_999_998UL)]
    public void Given_UnknownEvenTypeInRange_When_Parsing_Then_ItIsRejected(ulong type)
    {
        // Arrange
        var stream = Bolt12TestData.OfferBuilder().Set(type, new byte[] { 1 }).Build();

        // Act
        var ok = Offer.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.TlvStream, violation!.RequirementId);
        Assert.Equal(type, violation.Field);
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(79UL)]
    [InlineData(1_000_000_001UL)]
    [InlineData(1_999_999_999UL)]
    public void Given_UnknownOddTypeInRange_When_Parsing_Then_ItIsKept(ulong type)
    {
        // Arrange
        var stream = Bolt12TestData.OfferBuilder().Set(type, new byte[] { 1, 2 }).Build();

        // Act
        var offer = Offer.Parse(stream);

        // Assert
        Assert.True(offer.Stream.TryGetValue(type, out var value));
        Assert.Equal([1, 2], value.ToArray());
        Assert.Null(OfferValidator.Validate(offer));
    }

    [Fact]
    public void Given_PathWithZeroHops_When_Parsing_Then_ItBreaksTheOfferReaderRule()
    {
        // Arrange: first_node_id, first_path_key, num_hops = 0
        var path = Bolt12TestData.PayerId.ToString() + Bolt12TestData.PathKey + "00";
        var stream = Bolt12TestData.OfferBuilder().Set(Bolt12TlvTypes.OfferPaths, Convert.FromHexString(path)).Build();

        // Act
        var ok = Offer.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.OfferReader, violation!.RequirementId);
        Assert.Equal(Bolt12TlvTypes.OfferPaths, violation.Field);
    }

    [Fact]
    public void Given_SecondPathWithZeroHops_When_Parsing_Then_ItBreaksTheOfferReaderRule()
    {
        // Arrange: the second path has a sciddir first_node_id (direction 1, 1x2x3), first_path_key, num_hops = 0
        var good = BlindedPathCodec.Encode(Bolt12TestData.Path());
        var empty = Convert.FromHexString("01" + "0000010000020003" + Bolt12TestData.PathKey + "00");
        var stream = Bolt12TestData.OfferBuilder().Set(Bolt12TlvTypes.OfferPaths, [.. good, .. empty]).Build();

        // Act
        var ok = Offer.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.OfferReader, violation!.RequirementId);
    }

    public static TheoryData<string, string> OffCurvePaths()
    {
        var offCurve = Convert.ToHexString(Bolt12TestData.OffCurvePoint);
        var good = Bolt12TestData.PathKey.ToString();
        return new TheoryData<string, string>
        {
            { "first_node_id", offCurve + good + "01" + good + "0000" },
            { "first_path_key", good + offCurve + "01" + good + "0000" },
            { "blinded_node_id", good + good + "01" + offCurve + "0000" }
        };
    }

    [Theory]
    [MemberData(nameof(OffCurvePaths))]
    public void Given_PathPointOffTheCurve_When_Parsing_Then_ItIsMalformed(string point, string pathHex)
    {
        // Arrange
        var stream = Bolt12TestData.OfferBuilder()
                                   .Set(Bolt12TlvTypes.OfferPaths, Convert.FromHexString(pathHex))
                                   .Build();

        // Act
        var ok = Offer.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.TlvStream, violation!.RequirementId);
        Assert.Contains(point, violation.Reason);
    }

    [Theory]
    [InlineData(Bolt12TlvTypes.OfferChains, "00")]
    [InlineData(Bolt12TlvTypes.OfferCurrency, "c3")]
    [InlineData(Bolt12TlvTypes.OfferAmount, "0001")]
    [InlineData(Bolt12TlvTypes.OfferAmount, "010203040506070809")]
    [InlineData(Bolt12TlvTypes.OfferDescription, "ff")]
    [InlineData(Bolt12TlvTypes.OfferAbsoluteExpiry, "00")]
    [InlineData(Bolt12TlvTypes.OfferPaths, "02")]
    [InlineData(Bolt12TlvTypes.OfferIssuer, "80")]
    [InlineData(Bolt12TlvTypes.OfferQuantityMax, "0000")]
    [InlineData(Bolt12TlvTypes.OfferIssuerId, "02eec7")]
    [InlineData(Bolt12TlvTypes.OfferIssuerId, "04eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")]
    [InlineData(Bolt12TlvTypes.OfferIssuerId, "02ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")]
    public void Given_MalformedFieldValue_When_Parsing_Then_ItIsMalformed(ulong type, string valueHex)
    {
        // Arrange
        var stream = Bolt12TestData.OfferBuilder().Set(type, Convert.FromHexString(valueHex)).Build();

        // Act
        var ok = Offer.TryParse(stream, out _, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Equal(Bolt12RequirementIds.TlvStream, violation!.RequirementId);
        Assert.Equal(type, violation.Field);
        Assert.Throws<FormatException>(() => Offer.Parse(stream));
    }

    [Theory]
    [InlineData("lnr1qqqq", Bolt12RequirementIds.Encoding)]
    [InlineData("lno1pg", Bolt12RequirementIds.TlvStream)]
    [InlineData("lno1qq+", Bolt12RequirementIds.Continuation)]
    [InlineData("Lno1qqqq", Bolt12RequirementIds.Continuation)]
    [InlineData("lno1qqqb", Bolt12RequirementIds.Encoding)]
    public void Given_BadString_When_Parsing_Then_TheRequirementIsReported(string text, string requirementId)
    {
        // Act
        var ok = Offer.TryParse(text, out var offer, out var violation);

        // Assert
        Assert.False(ok);
        Assert.Null(offer);
        Assert.Equal(requirementId, violation!.RequirementId);
    }

    [Fact]
    public void Given_PayInfo_When_BuildingAnInvoice_Then_ItRoundTripsWithFeatures()
    {
        // Arrange
        var payInfo = new BlindedPayInfo(uint.MaxValue, 7, ushort.MaxValue, 5, ulong.MaxValue, new byte[] { 0x02, 0x00 });
        var stream = Bolt12TestData.InvoiceBuilder()
                                   .SetPayInfos(Bolt12TlvTypes.InvoiceBlindedPay, [payInfo])
                                   .Build();

        // Act
        var invoice = Bolt12Invoice.Parse(stream);

        // Assert
        var read = Assert.Single(invoice.Fields.BlindedPay!);
        Assert.Equal(payInfo with { Features = default }, read with { Features = default });
        Assert.Equal([0x02, 0x00], read.Features.ToArray());
    }
}