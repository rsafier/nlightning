namespace NLightning.Domain.Tests.Offers.Encoding;

using Domain.Offers.Encoding;

public class Bolt12Bech32Tests
{
    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("ff")]
    [InlineData("0102030405")]
    [InlineData("162102eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619")]
    public void Given_Bytes_When_EncodingAndDecoding_Then_TheyRoundTripInBothCases(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var lower = Bolt12Bech32.Encode("lno", bytes);
        var upper = Bolt12Bech32.Encode("lno", bytes, uppercase: true);

        // Assert
        Assert.Equal(lower.ToUpperInvariant(), upper);
        Assert.Equal("lno", Bolt12Bech32.Decode(lower).Hrp);
        Assert.Equal(bytes, Bolt12Bech32.Decode(lower).Data);
        Assert.Equal("lno", Bolt12Bech32.Decode(upper).Hrp);
        Assert.Equal(bytes, Bolt12Bech32.Decode(upper).Data);
    }

    [Fact]
    public void Given_KnownOffer_When_Encoding_Then_ItMatchesTheSpecString()
    {
        // Arrange: offers-test.json "Minimal bolt12 offer"
        var bytes = Convert.FromHexString("162102eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619");

        // Act
        var text = Bolt12Bech32.Encode("lno", bytes);

        // Assert
        Assert.Equal("lno1zcss9mk8y3wkklfvevcrszlmu23kfrxh49px20665dqwmn4p72pksese", text);
    }

    [Theory]
    [InlineData("lno1qq+qq")]
    [InlineData("lno1qq+ qq")]
    [InlineData("lno1qq+\r\n\t qq")]
    [InlineData("l+no1qqqq")]
    [InlineData("lno+1qqqq")]
    public void Given_PlusBetweenCharacters_When_Decoding_Then_ItIsRemoved(string text)
    {
        // Act
        var (hrp, data) = Bolt12Bech32.Decode(text);

        // Assert
        Assert.Equal("lno", hrp);
        Assert.Equal(Bolt12Bech32.Decode("lno1qqqq").Data, data);
    }

    [Theory]
    [InlineData("lno1qqqq+")]
    [InlineData("lno1qqqq+ ")]
    [InlineData("+lno1qqqq")]
    [InlineData("+ lno1qqqq")]
    [InlineData("lno1qq++qq")]
    [InlineData("lno1qq+ +qq")]
    [InlineData("lno1qq qq")]
    [InlineData(" lno1qqqq")]
    [InlineData("lno1QqQq")]
    public void Given_BadContinuationOrCase_When_Decoding_Then_ItIsRefused(string text)
    {
        // Act
        var ok = Bolt12Bech32.TryDecode(text, out _, out _, out var reason);

        // Assert
        Assert.False(ok);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("qqqq")]
    [InlineData("1qqqq")]
    [InlineData("lno1qqqb")]
    [InlineData("lno1qqqi")]
    [InlineData("lno1qqqo")]
    [InlineData("lno1q")]
    [InlineData("lno1qqqqp")]
    [InlineData("lno1pp")]
    [InlineData("lno1qé")]
    public void Given_BadBech32_When_Decoding_Then_ItIsRefused(string? text)
    {
        // Act
        var ok = Bolt12Bech32.TryDecode(text, out _, out _, out var reason);

        // Assert
        Assert.False(ok);
        Assert.NotNull(reason);
        Assert.Throws<FormatException>(() => Bolt12Bech32.Decode(text!));
    }

    [Fact]
    public void Given_EmptyData_When_Decoding_Then_ItIsEmpty()
    {
        // Act
        var (hrp, data) = Bolt12Bech32.Decode("LNO1");

        // Assert
        Assert.Equal("lno", hrp);
        Assert.Empty(data);
    }

    [Theory]
    [InlineData("")]
    [InlineData("LNO")]
    [InlineData("ln o")]
    public void Given_BadHrp_When_Encoding_Then_ItThrows(string hrp)
    {
        // Act & Assert
        Assert.ThrowsAny<ArgumentException>(() => Bolt12Bech32.Encode(hrp, [1]));
    }
}