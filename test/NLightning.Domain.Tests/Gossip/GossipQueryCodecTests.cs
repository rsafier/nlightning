namespace NLightning.Domain.Tests.Gossip;

using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Queries;

public class GossipQueryCodecTests
{
    [Fact]
    public void Given_ShortChannelIds_When_EncodedAndDecoded_Then_TheyRoundTripWithEncodingZero()
    {
        // Arrange
        ShortChannelId[] ids = [new(103, 1, 0), new(700_000, 2_000, 1), new(ShortChannelId.MaxThreeByteValue, 7, 65535)];

        // Act
        var encoded = GossipQueryCodec.EncodeShortChannelIds(ids);
        var decoded = GossipQueryCodec.DecodeShortChannelIds(encoded, "test");

        // Assert
        Assert.Equal(1 + 3 * ShortChannelId.Length, encoded.Length);
        Assert.Equal(GossipQueryCodec.EncodingUncompressed, encoded[0]);
        Assert.Equal(ids, decoded);
        Assert.Equal("0000670000010000", Convert.ToHexString(encoded, 1, 8));
    }

    [Theory]
    [InlineData("", "no encoding type")]
    [InlineData("01789c", "zlib")]
    [InlineData("02", "unknown")]
    [InlineData("00000067000001", "whole number")]
    public void Given_BadEncodedShortIds_When_Decoded_Then_AWarningNamesTheProblem(string hex, string expected)
    {
        // Act
        var exception = Assert.Throws<WarningException>(() =>
                                                            GossipQueryCodec.DecodeShortChannelIds(
                                                                Convert.FromHexString(hex), "query_short_channel_ids"));

        // Assert
        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Given_QueryFlags_When_EncodedAndDecoded_Then_EachFlagIsOneMinimalBigSize()
    {
        // Arrange
        ulong[] flags = [GossipQueryCodec.QueryFlagAll, GossipQueryCodec.QueryFlagChannelUpdate1, 0, 0xFD];

        // Act
        var encoded = GossipQueryCodec.EncodeQueryFlags(flags);
        var decoded = GossipQueryCodec.DecodeQueryFlags(encoded, flags.Length);

        // Assert
        Assert.Equal("001F0200FD00FD", Convert.ToHexString(encoded));
        Assert.Equal(flags, decoded);
    }

    [Theory]
    [InlineData("001F", 2)]
    [InlineData("001F1F", 1)]
    [InlineData("00FD001F", 1)] // 0x1F in three bytes: not minimal
    [InlineData("011F", 1)]
    [InlineData("", 0)]
    public void Given_BadQueryFlags_When_Decoded_Then_AWarning(string hex, int count)
    {
        // Act / Assert
        Assert.Throws<WarningException>(() => GossipQueryCodec.DecodeQueryFlags(Convert.FromHexString(hex), count));
    }

    [Fact]
    public void Given_TimestampsAndChecksums_When_EncodedAndDecoded_Then_TheyRoundTrip()
    {
        // Arrange
        ChannelUpdatePair[] pairs = [new(1_700_000_000, 0), new(0, 0xFFFFFFFF)];

        // Act
        var timestamps = GossipQueryCodec.EncodeTimestamps(pairs);
        var checksums = GossipQueryCodec.EncodeChecksums(pairs);

        // Assert
        Assert.Equal("006553F1000000000000000000FFFFFFFF", Convert.ToHexString(timestamps));
        Assert.Equal(pairs, GossipQueryCodec.DecodeTimestamps(timestamps, 2));
        Assert.Equal(16, checksums.Length);
        Assert.Equal(pairs, GossipQueryCodec.DecodeChecksums(checksums, 2));
        Assert.Throws<WarningException>(() => GossipQueryCodec.DecodeTimestamps(timestamps, 3));
        Assert.Throws<WarningException>(() => GossipQueryCodec.DecodeChecksums(checksums, 1));
    }

    [Theory]
    [InlineData("01", 1UL)]
    [InlineData("03", 3UL)]
    public void Given_AQueryOption_When_Decoded_Then_TheFlagsAreRead(string hex, ulong expected)
    {
        // Act / Assert
        Assert.Equal(expected, GossipQueryCodec.DecodeQueryOption(Convert.FromHexString(hex)));
        Assert.Equal(hex, Convert.ToHexString(GossipQueryCodec.EncodeQueryOption(expected)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0101")]
    [InlineData("FD0001")]
    public void Given_AMalformedQueryOption_When_Decoded_Then_AWarning(string hex)
    {
        // Act / Assert
        Assert.Throws<WarningException>(() => GossipQueryCodec.DecodeQueryOption(Convert.FromHexString(hex)));
    }

    [Theory]
    [InlineData(100u, 50u, 99u, false)]
    [InlineData(100u, 50u, 100u, true)]
    [InlineData(100u, 50u, 149u, true)]
    [InlineData(100u, 50u, 150u, false)]
    [InlineData(0u, uint.MaxValue, uint.MaxValue - 1, true)]
    [InlineData(0u, uint.MaxValue, uint.MaxValue, false)]
    [InlineData(uint.MaxValue, 0u, uint.MaxValue, false)]
    [InlineData(uint.MaxValue - 1, 10u, uint.MaxValue, true)]
    public void Given_AFilter_When_ATimestampIsChecked_Then_TheWindowIsFirstInclusiveEndExclusive(
        uint first, uint range, uint timestamp, bool expected)
    {
        // Act / Assert: B7-Q-05, first <= ts < first + range without overflow
        Assert.Equal(expected, new GossipTimestampFilter(first, range).Includes(timestamp));
    }

    [Fact]
    public void Given_TheNoneFilter_When_Checked_Then_NothingPasses()
    {
        // Act / Assert: B7-Q-06
        Assert.Equal(new GossipTimestampFilter(uint.MaxValue, 0), GossipTimestampFilter.None);
        Assert.False(GossipTimestampFilter.None.Includes(0));
        Assert.False(GossipTimestampFilter.None.Includes(uint.MaxValue));
    }
}