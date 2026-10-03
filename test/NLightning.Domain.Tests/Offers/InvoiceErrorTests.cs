namespace NLightning.Domain.Tests.Offers;

using Domain.Offers;
using Domain.Offers.Validators;

public class InvoiceErrorTests
{
    [Fact]
    public void Given_ErrorWithFieldAndSuggestion_When_Encoded_Then_TheBytesAreTheSpecLayout()
    {
        // Arrange
        var error = new InvoiceError("bad amount", 82, new byte[] { 0x27, 0x10 });

        // Act
        var bytes = error.Encode();

        // Assert: 1 = tu64 82, 3 = 0x2710, 5 = "bad amount"
        Assert.Equal(Convert.FromHexString("0101520302271005" + "0a" + "62616420616d6f756e74"), bytes);
    }

    [Fact]
    public void Given_EncodedError_When_Parsed_Then_EveryFieldRoundTrips()
    {
        // Arrange
        var bytes = new InvoiceError("nltg-m6-proof", 88, new byte[] { 1 }).Encode();

        // Act
        var parsed = InvoiceError.Parse(bytes);

        // Assert
        Assert.Equal("nltg-m6-proof", parsed.Error);
        Assert.Equal(88UL, parsed.ErroneousField);
        Assert.Equal([1], parsed.SuggestedValue!.Value.ToArray());
        Assert.Equal(bytes, parsed.Encode());
        Assert.Equal("nltg-m6-proof (field 88)", parsed.ToString());
    }

    [Fact]
    public void Given_ErrorOnly_When_Parsed_Then_TheOtherFieldsAreAbsent()
    {
        // Arrange
        var bytes = new InvoiceError("nope").Encode();

        // Act
        var parsed = InvoiceError.Parse(bytes);

        // Assert
        Assert.Equal(Convert.FromHexString("05046e6f7065"), bytes);
        Assert.Null(parsed.ErroneousField);
        Assert.Null(parsed.SuggestedValue);
        Assert.Equal("nope", parsed.ToString());
    }

    [Theory]
    [InlineData("05016e0701ff", true)]
    [InlineData("05016e0601ff", false)]
    [InlineData("0501ff", false)]
    [InlineData("010100", false)]
    [InlineData("05", false)]
    [InlineData("", true)]
    [InlineData("0301ff", true)]
    public void Given_InvoiceErrorBytes_When_Parsing_Then_OnlyTheFormatIsEnforced(string hex, bool accepted)
    {
        // Act
        var ok = InvoiceError.TryParse(Convert.FromHexString(hex), out var parsed, out var violation);

        // Assert
        Assert.Equal(accepted, ok);
        if (!ok)
        {
            Assert.Equal(Bolt12RequirementIds.TlvStream, violation!.RequirementId);
            Assert.Throws<FormatException>(() => InvoiceError.Parse(Convert.FromHexString(hex)));
        }
        else
        {
            Assert.NotNull(parsed);
        }
    }

    [Fact]
    public void Given_WriterRulesBroken_When_Constructing_Then_ItThrows()
    {
        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => new InvoiceError(""));
        Assert.Throws<ArgumentException>(() => new InvoiceError("x", null, new byte[] { 1 }));
    }
}