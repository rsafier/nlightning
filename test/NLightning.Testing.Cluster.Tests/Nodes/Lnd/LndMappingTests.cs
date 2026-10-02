using Google.Protobuf;
using Lnrpc;

namespace NLightning.Testing.Cluster.Tests.Nodes.Lnd;

using Cluster.Nodes.Lnd;

public class LndMappingTests
{
    private const string DisplayTxId = "fa38d188be25bc0647f5cdb65f1392c5e720b6b6fae5ebfd8907a4d458e68777";

    [Fact]
    public void Given_InternalOrderBytes_When_Converted_Then_TheTxIdIsInDisplayOrder()
    {
        // Arrange
        var internalOrder = Convert.FromHexString(DisplayTxId).Reverse().ToArray();

        // Act
        var txId = LndMapping.TxIdFromInternalBytes(internalOrder);

        // Assert
        Assert.Equal(DisplayTxId, txId);
    }

    [Fact]
    public void Given_AShortTxId_When_Converted_Then_ItThrows()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => LndMapping.TxIdFromInternalBytes(new byte[31]));
    }

    [Fact]
    public void Given_ChannelPointsInBothForms_When_TheirTxIdIsRead_Then_ItIsTheDisplayTxId()
    {
        // Arrange
        var bytesForm = new ChannelPoint
        {
            FundingTxidBytes = ByteString.CopyFrom(Convert.FromHexString(DisplayTxId).Reverse().ToArray()),
            OutputIndex = 1
        };
        var stringForm = LndMapping.ToChannelPoint(DisplayTxId.ToUpperInvariant(), 1);

        // Act & Assert
        Assert.Equal(DisplayTxId, LndMapping.TxIdOf(bytesForm));
        Assert.Equal(DisplayTxId, LndMapping.TxIdOf(stringForm));
        Assert.Throws<ArgumentException>(() => LndMapping.TxIdOf(new ChannelPoint()));
    }

    [Fact]
    public void Given_AChannelPointString_When_Parsed_Then_TxIdAndIndexAreSplit()
    {
        // Act
        var (txId, index) = LndMapping.ParseChannelPoint($"{DisplayTxId}:12");

        // Assert
        Assert.Equal(DisplayTxId, txId);
        Assert.Equal(12, index);
    }

    [Theory]
    [InlineData("abc:0")]
    [InlineData(DisplayTxId)]
    [InlineData(DisplayTxId + ":x")]
    [InlineData(DisplayTxId + ":-1")]
    public void Given_AMalformedChannelPoint_When_Parsed_Then_ItThrows(string channelPoint)
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => LndMapping.ParseChannelPoint(channelPoint));
    }

    [Theory]
    [InlineData(0UL, null)]
    [InlineData((103UL << 40) | (1UL << 16), "103x1x0")]
    [InlineData((700000UL << 40) | (2345UL << 16) | 3UL, "700000x2345x3")]
    public void Given_AChanId_When_Formatted_Then_ItIsBlockTxOutput(ulong chanId, string? expected)
    {
        // Act & Assert
        Assert.Equal(expected, LndMapping.FormatShortChannelId(chanId));
    }

    [Fact]
    public void Given_PushAmounts_When_ConvertedToSatoshis_Then_OnlyWholeNonNegativeSatoshisPass()
    {
        // Act & Assert
        Assert.Equal(0, LndMapping.ToWholeSatoshis(0, "push"));
        Assert.Equal(25_000, LndMapping.ToWholeSatoshis(25_000_000, "push"));
        Assert.Throws<ArgumentException>(() => LndMapping.ToWholeSatoshis(1_500, "push"));
        Assert.Throws<ArgumentException>(() => LndMapping.ToWholeSatoshis(-1_000, "push"));
    }

    [Fact]
    public void Given_AnLndChannel_When_Mapped_Then_TheFacadeChannelCarriesMsatAndTheScid()
    {
        // Arrange
        var channel = new Channel
        {
            RemotePubkey = "02" + new string('a', 64),
            ChannelPoint = $"{DisplayTxId}:0",
            ChanId = (103UL << 40) | (1UL << 16),
            Capacity = 1_000_000,
            LocalBalance = 30_000,
            Active = true
        };

        // Act
        var mapped = LndMapping.ToTestChannel(channel);

        // Assert
        Assert.Equal(channel.RemotePubkey, mapped.RemoteNodeId);
        Assert.Equal(DisplayTxId, mapped.FundingTxId);
        Assert.Equal(0, mapped.OutputIndex);
        Assert.Equal("103x1x0", mapped.ShortChannelId);
        Assert.Equal(1_000_000, mapped.CapacitySat);
        Assert.Equal(30_000_000, mapped.LocalBalanceMsat);
        Assert.True(mapped.Active);
    }

    [Fact]
    public void Given_Bytes_When_HexEncoded_Then_TheyAreLowerCase()
    {
        // Act & Assert
        Assert.Equal("00abff", LndMapping.ToHex(ByteString.CopyFrom(0x00, 0xab, 0xff)));
    }
}