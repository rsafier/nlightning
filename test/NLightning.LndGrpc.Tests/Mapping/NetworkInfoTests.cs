namespace NLightning.LndGrpc.Tests.Mapping;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;
using LndGrpc.Services;

/// <summary><c>GetNetworkInfo</c>'s figures as LND computes them (NL-1246).</summary>
public class NetworkInfoTests
{
    private const ulong Now = 2_000_000_000;

    private static CompactPubKey Key(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }

    private static GraphChannel Channel(ShortChannelId scid, CompactPubKey a, CompactPubKey b, ulong capacitySat,
                                        uint timestamp)
    {
        var channel = new GraphChannel(scid, a, b, null, null, capacitySat);
        foreach (var direction in new byte[] { 0, 1 })
            channel = channel.WithPolicy(new GraphPolicy(timestamp, 1, direction, 40, 1, capacitySat * 1_000, 1_000,
                                                         1));
        return channel;
    }

    [Fact]
    public void Given_LiveStaleAndSpentChannels_When_Computed_Then_LndsFiguresOverTheLiveOnes()
    {
        // Arrange: A-B 100k, B-C 300k, C-D 200k live; A-C with policies a month old (a zombie); B-D spent
        var (a, b, c, d) = (Key(1), Key(2), Key(3), Key(4));
        var fresh = (uint)(Now - 3_600);
        var stale = (uint)(Now - 30 * 86_400);
        var channels = new[]
        {
            Channel(new ShortChannelId(100, 1, 0), a, b, 100_000, fresh),
            Channel(new ShortChannelId(100, 2, 0), b, c, 300_000, fresh),
            Channel(new ShortChannelId(100, 3, 0), c, d, 200_000, fresh),
            Channel(new ShortChannelId(100, 4, 0), a, c, 500_000, stale),
            Channel(new ShortChannelId(100, 5, 0), b, d, 700_000, fresh).WithSpentAtHeight(150)
        };
        var graph = new GraphSnapshot(channels, []);

        // Act
        var info = LightningService.ComputeNetworkInfo(graph, Now, TimeSpan.FromDays(14), null);

        // Assert
        Assert.Equal(4u, info.NumNodes);
        Assert.Equal(3u, info.NumChannels);
        Assert.Equal(600_000, info.TotalNetworkCapacity);
        Assert.Equal(200_000, info.AvgChannelSize);
        Assert.Equal(100_000, info.MinChannelSize);
        Assert.Equal(300_000, info.MaxChannelSize);
        Assert.Equal(200_000, info.MedianChannelSizeSat);
        Assert.Equal(2u, info.MaxOutDegree);
        Assert.Equal(1.5, info.AvgOutDegree);
        Assert.Equal(1ul, info.NumZombieChans);
        Assert.Equal(0u, info.GraphDiameter);
    }

    [Fact]
    public void Given_AnEvenCountAndAnEmptyGraph_When_Computed_Then_TheMedianIsTheMiddleMeanAndEmptyIsZeros()
    {
        // Arrange
        var (a, b, c) = (Key(1), Key(2), Key(3));
        var fresh = (uint)(Now - 60);
        var even = new GraphSnapshot([
            Channel(new ShortChannelId(100, 1, 0), a, b, 100_000, fresh),
            Channel(new ShortChannelId(100, 2, 0), b, c, 301_000, fresh)
        ], []);

        // Act
        var info = LightningService.ComputeNetworkInfo(even, Now, TimeSpan.FromDays(14), null);
        var empty = LightningService.ComputeNetworkInfo(GraphSnapshot.Empty, Now, TimeSpan.FromDays(14), null);

        // Assert
        Assert.Equal(200_500, info.MedianChannelSizeSat);
        Assert.Equal((0u, 0u, 0L, 0.0, 0L), (empty.NumNodes, empty.NumChannels, empty.MinChannelSize,
                                              empty.AvgChannelSize, empty.MedianChannelSizeSat));
    }
}