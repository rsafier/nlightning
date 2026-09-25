namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Infrastructure.Bitcoin.Onion;

public class SphinxBigSizeTests
{
    // BOLT 1 Appendix A encoding vectors
    [Theory]
    [InlineData(0UL, "00")]
    [InlineData(252UL, "fc")]
    [InlineData(253UL, "fd00fd")]
    [InlineData(65535UL, "fdffff")]
    [InlineData(65536UL, "fe00010000")]
    [InlineData(4294967295UL, "feffffffff")]
    [InlineData(4294967296UL, "ff0000000100000000")]
    [InlineData(18446744073709551615UL, "ffffffffffffffffff")]
    public void Given_Value_When_WritingAndReading_Then_MatchesBolt1Encoding(ulong value, string hex)
    {
        // Arrange
        var expected = Convert.FromHexString(hex);
        var buffer = new byte[9];

        // Act
        var written = SphinxBigSize.Write(value, buffer);
        var ok = SphinxBigSize.TryRead(expected, out var read, out var bytesRead);

        // Assert
        Assert.Equal(expected.Length, SphinxBigSize.GetEncodedLength(value));
        Assert.Equal(expected, buffer[..written]);
        Assert.True(ok);
        Assert.Equal(value, read);
        Assert.Equal(expected.Length, bytesRead);
    }

    // BOLT 1 Appendix A decoding failures
    [Theory]
    [InlineData("fd00fc")] // not canonical
    [InlineData("fe0000ffff")] // not canonical
    [InlineData("ff00000000ffffffff")] // not canonical
    [InlineData("fd00")] // short read
    [InlineData("feffff")] // short read
    [InlineData("ffffffffff")] // short read
    [InlineData("")] // empty
    public void Given_InvalidEncoding_When_Reading_Then_ReturnsFalse(string hex)
    {
        // Act
        var ok = SphinxBigSize.TryRead(Convert.FromHexString(hex), out _, out _);

        // Assert
        Assert.False(ok);
    }

    [Fact]
    public void Given_ShortDestination_When_Writing_Then_ThrowsArgumentException()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => SphinxBigSize.Write(253, new byte[2]));
    }
}