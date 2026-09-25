namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters.Onion;

using Infrastructure.Protocol.Tlv.Converters.Onion;

public class OnionTruncatedIntTests
{
    // BOLT 1 Appendix B tu64 vectors (n1 type 1) and edge cases
    [Theory]
    [InlineData("", 0UL)]
    [InlineData("01", 1UL)]
    [InlineData("0100", 256UL)]
    [InlineData("010000", 65536UL)]
    [InlineData("01000000", 16777216UL)]
    [InlineData("0100000000", 4294967296UL)]
    [InlineData("010000000000", 1099511627776UL)]
    [InlineData("01000000000000", 281474976710656UL)]
    [InlineData("0100000000000000", 72057594037927936UL)]
    [InlineData("ffffffffffffffff", ulong.MaxValue)]
    public void Given_Tu64Value_When_EncodingAndDecoding_Then_RoundTrips(string hex, ulong value)
    {
        // Arrange
        var expected = Convert.FromHexString(hex);

        // Act
        var encoded = OnionTruncatedInt.Encode(value);
        var ok = OnionTruncatedInt.TryDecodeTu64(expected, out var decoded);

        // Assert
        Assert.Equal(expected, encoded);
        Assert.True(ok);
        Assert.Equal(value, decoded);
    }

    // BOLT 1 Appendix B "TLV decoding failures": non-minimal tu64 encodings
    [Theory]
    [InlineData("00")]
    [InlineData("0001")]
    [InlineData("000100")]
    [InlineData("00010000")]
    [InlineData("0001000000")]
    [InlineData("000100000000")]
    [InlineData("00010000000000")]
    [InlineData("0001000000000000")]
    [InlineData("010000000000000000")]
    public void Given_InvalidTu64_When_Decoding_Then_Fails(string hex)
    {
        // Act
        var ok = OnionTruncatedInt.TryDecodeTu64(Convert.FromHexString(hex), out _);

        // Assert
        Assert.False(ok);
    }

    [Theory]
    [InlineData("", 0U)]
    [InlineData("90", 144U)]
    [InlineData("ffffffff", uint.MaxValue)]
    public void Given_ValidTu32_When_Decoding_Then_Succeeds(string hex, uint expected)
    {
        // Act
        var ok = OnionTruncatedInt.TryDecodeTu32(Convert.FromHexString(hex), out var value);

        // Assert
        Assert.True(ok);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("0090")]
    [InlineData("0100000000")]
    public void Given_InvalidTu32_When_Decoding_Then_Fails(string hex)
    {
        // Act
        var ok = OnionTruncatedInt.TryDecodeTu32(Convert.FromHexString(hex), out var value);

        // Assert
        Assert.False(ok);
        Assert.Equal(0U, value);
    }
}