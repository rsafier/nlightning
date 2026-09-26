namespace NLightning.Domain.Tests.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Graph;

/// <summary>
/// Builds synthetic graphs for the pathfinder tests. Node ids are shaped like compressed keys (not real points).
/// </summary>
internal sealed class GraphTestKit
{
    public const uint Timestamp = 1_700_000_000;

    private readonly Dictionary<string, CompactPubKey> _nodes = new();
    private readonly Dictionary<ShortChannelId, GraphChannel> _channels = new();
    private readonly List<GraphNode> _announcements = [];
    private uint _nextBlock = 100;

    public static CompactPubKey NodeId(int index)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[29] = (byte)(index >> 24);
        bytes[30] = (byte)(index >> 16);
        bytes[31] = (byte)(index >> 8);
        bytes[32] = (byte)index;
        return new CompactPubKey(bytes);
    }

    public CompactPubKey this[string name]
    {
        get
        {
            if (!_nodes.TryGetValue(name, out var id))
                _nodes[name] = id = NodeId(_nodes.Count + 1);

            return id;
        }
    }

    public static GraphPolicy Policy(uint feeBase = 0, uint feePpm = 0, ushort cltvDelta = 40, ulong htlcMin = 1,
                                     ulong htlcMax = 1_000_000_000, bool disabled = false,
                                     uint timestamp = Timestamp) =>
        new(timestamp, 1, disabled ? (byte)2 : (byte)0, cltvDelta, htlcMin, htlcMax, feeBase, feePpm);

    /// <summary>
    /// Adds a channel <paramref name="from"/>–<paramref name="to"/>; <paramref name="forward"/> is
    /// <paramref name="from"/>'s policy, <paramref name="backward"/> <paramref name="to"/>'s.
    /// </summary>
    public ShortChannelId Channel(string from, string to, GraphPolicy? forward, GraphPolicy? backward = null,
                                  ulong capacitySat = 10_000_000, byte[]? features = null,
                                  uint? spentAtHeight = null,
                                  GraphChannelVerification verification = GraphChannelVerification.Verified)
    {
        var scid = new ShortChannelId(_nextBlock++, 1, 0);
        var a = this[from];
        var b = this[to];
        var aIsNode1 = GraphChannel.CompareNodeIds(a, b) < 0;
        var (n1, n2) = aIsNode1 ? (a, b) : (b, a);
        var channel = new GraphChannel(scid, n1, n2, n1, n2, capacitySat, features ?? [], verification)
        {
            SpentAtHeight = spentAtHeight
        };

        if (forward is not null)
            channel = channel.WithPolicy(WithDirection(forward, aIsNode1 ? (byte)0 : (byte)1));
        if (backward is not null)
            channel = channel.WithPolicy(WithDirection(backward, aIsNode1 ? (byte)1 : (byte)0));

        _channels[scid] = channel;
        return scid;
    }

    public void Announce(string name, byte[]? features = null) =>
        _announcements.Add(new GraphNode(this[name], Timestamp, features ?? [], new byte[32], new byte[3]));

    public GraphSnapshot Build() => new(_channels.Values, _announcements);

    public static GraphPolicy WithDirection(GraphPolicy policy, byte direction) =>
        policy with { ChannelFlags = (byte)((policy.ChannelFlags & ~1) | direction) };
}