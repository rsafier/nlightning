namespace NLightning.Domain.Tests.Accounting;

using Domain.Accounting.Services;

public class AccountingDetailsCodecTests
{
    [Fact]
    public void Given_Details_When_EncodedAndDecoded_Then_TheyRoundTripInKeyOrder()
    {
        // Arrange
        var details = AccountingDetailsCodec.Create(("z", "last"), ("a", "first \"quoted\" ü"), ("skip", null));

        // Act
        var json = AccountingDetailsCodec.Encode(details);
        var decoded = AccountingDetailsCodec.Decode(json);

        // Assert
        Assert.NotNull(json);
        Assert.StartsWith("{\"a\":", json);
        Assert.Equal(["a", "z"], decoded.Keys);
        Assert.Equal("first \"quoted\" ü", decoded["a"]);
        Assert.False(decoded.ContainsKey("skip"));
    }

    [Fact]
    public void Given_NoDetails_When_Encoded_Then_ItIsNullAndDecodesEmpty()
    {
        // Act
        var json = AccountingDetailsCodec.Encode(AccountingDetailsCodec.Create());

        // Assert
        Assert.Null(json);
        Assert.Empty(AccountingDetailsCodec.Decode(json));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{\"a\":1}")]
    [InlineData("{not json")]
    public void Given_TextThatIsNotAFlatStringObject_When_Decoded_Then_ItThrowsFormatException(string json)
    {
        // Act / Assert
        Assert.Throws<FormatException>(() => AccountingDetailsCodec.Decode(json));
    }
}