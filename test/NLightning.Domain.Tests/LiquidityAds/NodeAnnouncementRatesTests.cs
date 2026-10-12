using NLightning.Tests.Utils.Vectors;

namespace NLightning.Domain.Tests.LiquidityAds;

using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Enums;
using Domain.LiquidityAds.Models;
using Domain.Protocol.Payloads;

public class NodeAnnouncementRatesTests
{
    private static readonly FundingRate s_rate1 = new(100_000, 500_000, 550, 100, 5_000, 1_000);
    private static readonly FundingRate s_rate2 = new(500_000, 5_000_000, 1_100, 75, 0, 1_500);

    private static readonly WillFundRates s_rates =
        WillFundRates.Create([s_rate1, s_rate2], [LiquidityPaymentType.FromChannelBalance]);

    [Fact]
    public void Given_EclairsRates_When_EncodedAsExtraData_Then_EqualsEclairsTlvStream()
    {
        // Act
        var extraData = NodeAnnouncementRates.EncodeExtraData(s_rates);

        // Assert: fd053b, length 0x2d, will_fund_rates (LightningMessageCodecsSpec "encode/decode liquidity ads")
        Assert.Equal("fd053b2d" + LiquidityAdsEclairVectors.NodeAnnouncementRates, Convert.ToHexStringLower(extraData));
    }

    [Fact]
    public void Given_NoRates_When_EncodedAsExtraData_Then_Empty()
    {
        // Act
        var extraData = NodeAnnouncementRates.EncodeExtraData(null);

        // Assert
        Assert.Empty(extraData);
    }

    [Fact]
    public void Given_EclairsExtraData_When_Read_Then_TheRatesAreReturned()
    {
        // Arrange
        var extraData = Convert.FromHexString("fd053b2d" + LiquidityAdsEclairVectors.NodeAnnouncementRates);

        // Act
        var read = NodeAnnouncementRates.TryRead(extraData, out var rates);

        // Assert
        Assert.True(read);
        Assert.NotNull(rates);
        Assert.Equal(s_rates, rates);
        Assert.True(rates.Supports(LiquidityPaymentType.FromChannelBalance));
    }

    [Fact]
    public void Given_EmptyExtraData_When_Read_Then_NoRates()
    {
        // Act
        var read = NodeAnnouncementRates.TryRead([], out var rates);

        // Assert
        Assert.False(read);
        Assert.Null(rates);
    }

    [Fact]
    public void Given_UnknownOddRecordsAroundTheRates_When_Read_Then_TheyAreSkipped()
    {
        // Arrange: type 1 (2 bytes) before, type 1341 (fd053d, 1 byte) after
        var extraData = Convert.FromHexString("0102abcd" + "fd053b2d" + LiquidityAdsEclairVectors.NodeAnnouncementRates
                                            + "fd053d01ff");

        // Act
        var read = NodeAnnouncementRates.TryRead(extraData, out var rates);

        // Assert
        Assert.True(read);
        Assert.Equal(s_rates, rates);
    }

    [Fact]
    public void Given_OnlyUnknownOddRecords_When_Read_Then_NoRates()
    {
        // Act
        var read = NodeAnnouncementRates.TryRead(Convert.FromHexString("0102abcd"), out var rates);

        // Assert
        Assert.False(read);
        Assert.Null(rates);
    }

    [Theory]
    [InlineData("0002abcd", "an unknown even type")]
    [InlineData("fd053b05aabbccddee", "rates that do not decode")]
    [InlineData("fd053b2d00", "a length past the end")]
    [InlineData("fd0001", "a type that is not a minimal bigsize")]
    [InlineData("fd053b", "no length")]
    [InlineData("0301aa0101bb", "types not increasing")]
    public void Given_AMalformedStream_When_Read_Then_NoRatesQuietly(string hex, string reason)
    {
        // Arrange
        var extraData = Convert.FromHexString(hex);

        // Act
        var read = NodeAnnouncementRates.TryRead(extraData, out var rates);

        // Assert
        Assert.False(read, reason);
        Assert.Null(rates);
    }

    [Fact]
    public void Given_RatesFollowedByGarbage_When_Read_Then_NoRates()
    {
        // Arrange: a valid record, then a truncated one
        var extraData = Convert.FromHexString("fd053b2d" + LiquidityAdsEclairVectors.NodeAnnouncementRates + "fd05");

        // Act
        var read = NodeAnnouncementRates.TryRead(extraData, out var rates);

        // Assert
        Assert.False(read);
        Assert.Null(rates);
    }

    [Fact]
    public void Given_EclairsNodeAnnouncement_When_ReadFromThePayload_Then_TheRatesAreReturned()
    {
        // Arrange: the payload without the message type, as GraphNode.RawAnnouncement stores it
        var payload = Convert.FromHexString(LiquidityAdsEclairVectors.NodeAnnouncementWire)[2..];

        // Act
        var read = NodeAnnouncementRates.TryReadFromAnnouncement(payload, out var rates);

        // Assert
        Assert.True(read);
        Assert.Equal(s_rates, rates);
    }

    [Fact]
    public void Given_AnAnnouncementWithoutExtraData_When_ReadFromThePayload_Then_NoRates()
    {
        // Arrange
        var key = new byte[33];
        key[0] = 0x02;
        key[32] = 0x01;
        var payload = new NodeAnnouncementPayload(NodeAnnouncementPayload.EmptySignature, ReadOnlyMemory<byte>.Empty,
                                                  1_700_000_000, new CompactPubKey(key), new byte[3],
                                                  NodeAnnouncementPayload.EncodeAlias("plain"),
                                                  ReadOnlyMemory<byte>.Empty).GetBytes();

        // Act
        var read = NodeAnnouncementRates.TryReadFromAnnouncement(payload, out var rates);

        // Assert
        Assert.False(read);
        Assert.Null(rates);
    }

    [Fact]
    public void Given_ATruncatedAnnouncement_When_ReadFromThePayload_Then_NoRates()
    {
        // Act
        var read = NodeAnnouncementRates.TryReadFromAnnouncement(new byte[10], out var rates);

        // Assert
        Assert.False(read);
        Assert.Null(rates);
    }

    [Fact]
    public void Given_OurRates_When_EncodedThenRead_Then_TheyRoundTrip()
    {
        // Arrange
        var ours = WillFundRates.Create([s_rate1], [LiquidityPaymentType.FromChannelBalance]);

        // Act
        var read = NodeAnnouncementRates.TryRead(NodeAnnouncementRates.EncodeExtraData(ours), out var rates);

        // Assert
        Assert.True(read);
        Assert.Equal(ours, rates);
    }
}