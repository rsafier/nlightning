namespace NLightning.Infrastructure.Tests.Converters;

using Infrastructure.Converters;

public class TruncatedIntTests
{
    // BOLT 1 Appendix B: n1 namespace tlv1 (tu64 amount_msat) decoding successes
    public static TheoryData<string, ulong> Tu64ValidVectors => new()
    {
        { "", 0UL },
        { "01", 1UL },
        { "0100", 256UL },
        { "010000", 65536UL },
        { "01000000", 16777216UL },
        { "0100000000", 4294967296UL },
        { "010000000000", 1099511627776UL },
        { "01000000000000", 281474976710656UL },
        { "0100000000000000", 72057594037927936UL }
    };

    // BOLT 1 Appendix B: n1 namespace tlv1 (tu64 amount_msat) decoding failures
    public static TheoryData<string> Tu64InvalidVectors => new()
    {
        "ffffffffffffffffff",
        "00",
        "0001",
        "000100",
        "00010000",
        "0001000000",
        "000100000000",
        "00010000000000",
        "0001000000000000"
    };

    [Theory]
    [MemberData(nameof(Tu64ValidVectors))]
    public void Given_Bolt1Tu64Vector_When_Decoded_Then_ValueIsKnown(string hex, ulong expected)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var value = TruncatedInt.DecodeTu64(bytes);

        // Assert
        Assert.Equal(expected, value);
        Assert.True(TruncatedInt.TryDecodeTu64(bytes, out var tryValue));
        Assert.Equal(expected, tryValue);
    }

    [Theory]
    [MemberData(nameof(Tu64ValidVectors))]
    public void Given_Bolt1Tu64Vector_When_Encoded_Then_BytesAreKnown(string hex, ulong value)
    {
        // Act
        var bytes = TruncatedInt.EncodeTu64(value);

        // Assert
        Assert.Equal(Convert.FromHexString(hex), bytes);
        Assert.Equal(bytes.Length, TruncatedInt.GetEncodedLength(value));
    }

    [Theory]
    [MemberData(nameof(Tu64InvalidVectors))]
    public void Given_Bolt1InvalidTu64Vector_When_Decoded_Then_Throws(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var exception = Record.Exception(() => TruncatedInt.DecodeTu64(bytes));

        // Assert
        Assert.IsType<FormatException>(exception);
        Assert.False(TruncatedInt.TryDecodeTu64(bytes, out _));
    }

    [Fact]
    public void Given_MaxValues_When_EncodedAndDecoded_Then_RoundTrips()
    {
        // Act
        var tu16 = TruncatedInt.EncodeTu16(ushort.MaxValue);
        var tu32 = TruncatedInt.EncodeTu32(uint.MaxValue);
        var tu64 = TruncatedInt.EncodeTu64(ulong.MaxValue);

        // Assert
        Assert.Equal(new byte[] { 0xff, 0xff }, tu16);
        Assert.Equal(new byte[] { 0xff, 0xff, 0xff, 0xff }, tu32);
        Assert.Equal(Enumerable.Repeat((byte)0xff, 8).ToArray(), tu64);
        Assert.Equal(ushort.MaxValue, TruncatedInt.DecodeTu16(tu16));
        Assert.Equal(uint.MaxValue, TruncatedInt.DecodeTu32(tu32));
        Assert.Equal(ulong.MaxValue, TruncatedInt.DecodeTu64(tu64));
    }

    [Fact]
    public void Given_Zero_When_EncodedAndDecoded_Then_IsEmpty()
    {
        // Act
        var tu16 = TruncatedInt.EncodeTu16(0);
        var tu32 = TruncatedInt.EncodeTu32(0);
        var tu64 = TruncatedInt.EncodeTu64(0);

        // Assert
        Assert.Empty(tu16);
        Assert.Empty(tu32);
        Assert.Empty(tu64);
        Assert.Equal(0, TruncatedInt.DecodeTu16([]));
        Assert.Equal(0U, TruncatedInt.DecodeTu32([]));
    }

    [Theory]
    [InlineData("000001")]
    [InlineData("0102ff")]
    public void Given_TooLongTu16_When_Decoded_Then_Throws(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var exception = Record.Exception(() => TruncatedInt.DecodeTu16(bytes));

        // Assert
        Assert.IsType<FormatException>(exception);
        Assert.False(TruncatedInt.TryDecodeTu16(bytes, out _));
    }

    [Theory]
    [InlineData("0000000001")]
    [InlineData("0102030405")]
    public void Given_TooLongTu32_When_Decoded_Then_Throws(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var exception = Record.Exception(() => TruncatedInt.DecodeTu32(bytes));

        // Assert
        Assert.IsType<FormatException>(exception);
        Assert.False(TruncatedInt.TryDecodeTu32(bytes, out _));
    }

    [Theory]
    [InlineData("00")]
    [InlineData("00ff")]
    public void Given_LeadingZeroTu16_When_Decoded_Then_Throws(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var exception = Record.Exception(() => TruncatedInt.DecodeTu16(bytes));

        // Assert
        Assert.IsType<FormatException>(exception);
        Assert.False(TruncatedInt.TryDecodeTu16(bytes, out _));
    }

    [Theory]
    [InlineData("00")]
    [InlineData("00ffffff")]
    public void Given_LeadingZeroTu32_When_Decoded_Then_Throws(string hex)
    {
        // Arrange
        var bytes = Convert.FromHexString(hex);

        // Act
        var exception = Record.Exception(() => TruncatedInt.DecodeTu32(bytes));

        // Assert
        Assert.IsType<FormatException>(exception);
        Assert.False(TruncatedInt.TryDecodeTu32(bytes, out _));
    }

    [Theory]
    [InlineData(0x01UL, "01")]
    [InlineData(0x0102UL, "0102")]
    [InlineData(0x01020304UL, "01020304")]
    [InlineData(0x0102030405060708UL, "0102030405060708")]
    public void Given_Value_When_EncodedToSpan_Then_WritesMinimalBytes(ulong value, string expectedHex)
    {
        // Arrange
        var destination = new byte[8];

        // Act
        var written = TruncatedInt.Encode(value, destination);

        // Assert
        Assert.Equal(Convert.FromHexString(expectedHex), destination[..written]);
    }

    [Fact]
    public void Given_SmallDestination_When_Encoded_Then_Throws()
    {
        // Arrange
        var destination = new byte[1];

        // Act
        var exception = Record.Exception(() => TruncatedInt.Encode(0x0100, destination));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }
}