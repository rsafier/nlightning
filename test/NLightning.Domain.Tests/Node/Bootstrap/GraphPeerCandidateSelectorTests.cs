using System.Net;

namespace NLightning.Domain.Tests.Node.Bootstrap;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Node.Bootstrap;

public class GraphPeerCandidateSelectorTests
{
    private const ulong Now = 2_000_000_000;

    private readonly List<GraphChannel> _channels = [];
    private readonly List<GraphNode> _nodes = [];
    private byte _nextKey;
    private ulong _nextScid;

    private CompactPubKey NewKey()
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = ++_nextKey;
        return new CompactPubKey(bytes);
    }

    private const string Onion = "duckduckgogg42xjoc72x3sjasowoarfbgcmvfimaftt6twagswzczad.onion";

    private CompactPubKey AddNode(string address, int channels, ulong announcedAgo = 0, ulong updatedAgo = 0) =>
        AddNode([AddressDescriptor.FromIpAddress(IPAddress.Parse(address), 9735)], channels, announcedAgo, updatedAgo);

    private CompactPubKey AddNode(List<AddressDescriptor> addresses, int channels, ulong announcedAgo = 0,
                                  ulong updatedAgo = 0)
    {
        var id = NewKey();
        _nodes.Add(new GraphNode(id, (uint)(Now - announcedAgo), ReadOnlyMemory<byte>.Empty, new byte[32],
                                 new byte[3], addresses));
        for (var i = 0; i < channels; i++)
        {
            var other = NewKey();
            var idFirst = GraphChannel.CompareNodeIds(id, other) < 0;
            var (node1, node2) = idFirst ? (id, other) : (other, id);
            var channel = new GraphChannel(new ShortChannelId(++_nextScid), node1, node2, node1, node2, 100_000);
            _channels.Add(channel.WithPolicy(new GraphPolicy((uint)(Now - updatedAgo), 1, (byte)(idFirst ? 0 : 1),
                                                             40, 1_000, 1_000_000_000, 1_000, 1)));
        }

        return id;
    }

    private List<SeedPeerCandidate> Select(int limit = 100, int seed = 1,
                                           OnionCandidates onions = OnionCandidates.None,
                                           HashSet<(string, ushort)>? failed = null) =>
        GraphPeerCandidateSelector.Select(new GraphSnapshot(_channels, _nodes), Now, new HashSet<CompactPubKey>(),
                                          failed ?? [], DnsSeedAddressTypes.Both, false, limit, new Random(seed),
                                          onions);

    [Fact]
    public void Given_GoodAndWeakerNodes_When_Selecting_Then_GoodNodesComeFirst()
    {
        // Arrange: good = recent announcement and 2+ active channels
        var oneChannel = AddNode("12.0.0.1", 1);
        var oldAnnouncement = AddNode("12.0.0.2", 3, announcedAgo: (ulong)TimeSpan.FromDays(20).TotalSeconds);
        var good1 = AddNode("12.0.0.3", 2);
        var good2 = AddNode("12.0.0.4", 5);

        // Act
        var candidates = Select();

        // Assert
        Assert.Equal(4, candidates.Count);
        Assert.Equal(new[] { good1, good2 }.ToHashSet(), candidates.Take(2).Select(c => c.NodeId).ToHashSet());
        Assert.Equal(new[] { oneChannel, oldAnnouncement }.ToHashSet(),
                     candidates.Skip(2).Select(c => c.NodeId).ToHashSet());
        Assert.All(candidates, c => Assert.Equal(GraphPeerCandidateSelector.GraphSource, c.Seed));
    }

    [Fact]
    public void Given_ManyGoodNodes_When_SelectingWithDifferentSeeds_Then_TheOrderIsRandomized()
    {
        // Arrange
        for (var i = 1; i <= 20; i++)
            AddNode($"12.0.1.{i}", 2);

        // Act
        var first = Select(seed: 1).Select(c => c.NodeId).ToList();
        var second = Select(seed: 2).Select(c => c.NodeId).ToList();

        // Assert
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Given_NodesSharingAnEndpointAndALimit_When_Selecting_Then_OnePerEndpointUpToTheLimit()
    {
        // Arrange
        for (var i = 0; i < 5; i++)
            AddNode("12.0.2.1", 2);
        for (var i = 1; i <= 5; i++)
            AddNode($"12.0.3.{i}", 2);

        // Act
        var all = Select();
        var limited = Select(limit: 3);

        // Assert
        Assert.Equal(6, all.Count);
        Assert.Single(all, c => c.Address.Equals(IPAddress.Parse("12.0.2.1")));
        Assert.Equal(3, limited.Count);
    }

    [Fact]
    public void Given_ANodeWithOnlyStaleUpdates_When_Selecting_Then_ItIsSkipped()
    {
        // Arrange
        AddNode("12.0.4.1", 3, updatedAgo: (ulong)TimeSpan.FromDays(15).TotalSeconds);
        var fresh = AddNode("12.0.4.2", 1, updatedAgo: (ulong)TimeSpan.FromDays(13).TotalSeconds);

        // Act
        var candidates = Select();

        // Assert
        Assert.Equal(fresh, Assert.Single(candidates).NodeId);
    }

    [Fact]
    public void Given_AnOnionOnlyNode_When_SelectingWithoutTor_Then_ItIsSkipped()
    {
        // Arrange
        AddNode([AddressDescriptor.FromHost(AddressDescriptorType.TorV3, Onion, 9735)], 3);

        // Act & Assert
        Assert.Empty(Select());
    }

    [Theory]
    [InlineData(OnionCandidates.Fallback)]
    [InlineData(OnionCandidates.Preferred)]
    public void Given_AnOnionOnlyNode_When_SelectingWithTor_Then_ItsOnionIsTheCandidate(OnionCandidates onions)
    {
        // Arrange
        var id = AddNode([AddressDescriptor.FromHost(AddressDescriptorType.TorV3, Onion, 9735)], 3);

        // Act
        var candidate = Assert.Single(Select(onions: onions));

        // Assert
        Assert.Equal(id, candidate.NodeId);
        Assert.Equal(Onion, candidate.OnionHost);
        Assert.Equal((Onion, (ushort)9735), candidate.Endpoint);
        Assert.EndsWith($"@{Onion}:9735", candidate.ToPeerAddressInfo().Address);
    }

    [Theory]
    [InlineData(OnionCandidates.Fallback, false)]
    [InlineData(OnionCandidates.Preferred, true)]
    public void Given_ANodeWithAnIpAndAnOnion_When_Selecting_Then_TheModeDecidesWhichIsDialed(OnionCandidates onions,
                                                                                               bool expectOnion)
    {
        // Arrange
        AddNode([AddressDescriptor.FromIpAddress(IPAddress.Parse("12.0.5.1"), 9735),
                 AddressDescriptor.FromHost(AddressDescriptorType.TorV3, Onion, 9735)], 3);

        // Act
        var candidate = Assert.Single(Select(onions: onions));

        // Assert
        Assert.Equal(expectOnion, candidate.OnionHost is not null);
    }

    [Fact]
    public void Given_AFailedOnion_When_Selecting_Then_TheNodesIpIsDialedInstead()
    {
        // Arrange
        AddNode([AddressDescriptor.FromIpAddress(IPAddress.Parse("12.0.6.1"), 9735),
                 AddressDescriptor.FromHost(AddressDescriptorType.TorV3, Onion, 9735)], 3);

        // Act
        var candidate = Assert.Single(Select(onions: OnionCandidates.Preferred, failed: [(Onion, 9735)]));

        // Assert
        Assert.Null(candidate.OnionHost);
        Assert.Equal(IPAddress.Parse("12.0.6.1"), candidate.Address);
    }

    [Fact]
    public void Given_AnOnionWithABadChecksum_When_Selecting_Then_ItIsSkipped()
    {
        // Arrange: FromHost does not check the checksum; the selector does
        var bad = Convert.FromHexString("1d04a1d04a338c6e6ae970bfabee49049d6702250984ca950c01673f4ec034ad000003");
        AddNode([new AddressDescriptor(AddressDescriptorType.TorV3, bad, 9735)], 3);

        // Act & Assert
        Assert.Empty(Select(onions: OnionCandidates.Preferred));
    }
}