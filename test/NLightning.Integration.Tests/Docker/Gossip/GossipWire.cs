using System.Buffers.Binary;

namespace NLightning.Integration.Tests.Docker.Gossip;

/// <summary>
/// Reads the fields a proof filters on straight from recorded gossip wire bytes (u16 type prefix included, as
/// <c>Capture/RawGossipRecorder</c> keeps them): the short channel id of a <c>channel_announcement</c>, the node id of
/// a <c>node_announcement</c>, and the short channel id and direction of a <c>channel_update</c> (BOLT 7 layouts). A
/// message too short for its fields is skipped (the node's own parse reports it).
/// </summary>
public static class GossipWire
{
    private const ushort ChannelAnnouncementType = 256;
    private const ushort NodeAnnouncementType = 257;
    private const ushort ChannelUpdateType = 258;
    private const int TypeLength = 2;
    private const int SignatureLength = 64;
    private const int ChainHashLength = 32;
    private const int ScidLength = 8;
    private const int NodeIdLength = 33;

    /// <summary>
    /// What a set of recorded messages announced.
    /// </summary>
    /// <param name="AnnouncedChannels">Short channel ids of the <c>channel_announcement</c>s.</param>
    /// <param name="AnnouncedNodes">Node ids (lowercase hex) of the <c>node_announcement</c>s.</param>
    /// <param name="Updates">Short channel id and direction bit of every <c>channel_update</c>.</param>
    public sealed record GossipSummary(IReadOnlySet<ulong> AnnouncedChannels, IReadOnlySet<string> AnnouncedNodes,
                                       IReadOnlySet<(ulong ShortChannelId, int Direction)> Updates)
    {
        public override string ToString() =>
            $"{AnnouncedChannels.Count} channel_announcement, {AnnouncedNodes.Count} node_announcement, "
          + $"{Updates.Count} channel_update (distinct)";
    }

    /// <summary>
    /// Summarizes <paramref name="wires"/> (each a whole message, type prefix included); other types are ignored.
    /// </summary>
    public static GossipSummary Summarize(IEnumerable<byte[]> wires)
    {
        var channels = new HashSet<ulong>();
        var nodes = new HashSet<string>();
        var updates = new HashSet<(ulong, int)>();
        foreach (var wire in wires)
        {
            if (wire.Length < TypeLength)
                continue;

            switch (BinaryPrimitives.ReadUInt16BigEndian(wire))
            {
                case ChannelAnnouncementType when TryReadChannelAnnouncementScid(wire, out var scid):
                    channels.Add(scid);
                    break;
                case NodeAnnouncementType when TryReadNodeId(wire, out var nodeId):
                    nodes.Add(nodeId);
                    break;
                case ChannelUpdateType when TryReadUpdate(wire, out var update):
                    updates.Add(update);
                    break;
            }
        }

        return new GossipSummary(channels, nodes, updates);
    }

    /// <summary>
    /// What <paramref name="summary"/> lacks of <paramref name="shortChannelIds"/> (announcement and both directions'
    /// updates) and <paramref name="nodeIds"/> (lowercase hex); empty when nothing is missing.
    /// </summary>
    public static IReadOnlyList<string> Missing(GossipSummary summary, IEnumerable<ulong> shortChannelIds,
                                                IEnumerable<string> nodeIds)
    {
        var missing = new List<string>();
        foreach (var scid in shortChannelIds)
        {
            if (!summary.AnnouncedChannels.Contains(scid))
                missing.Add($"channel_announcement {scid}");
            for (var direction = 0; direction < 2; direction++)
                if (!summary.Updates.Contains((scid, direction)))
                    missing.Add($"channel_update {scid}/{direction}");
        }

        missing.AddRange(nodeIds.Where(id => !summary.AnnouncedNodes.Contains(id))
                                .Select(id => $"node_announcement {id[..16]}…"));
        return missing;
    }

    private static bool TryReadChannelAnnouncementScid(byte[] wire, out ulong scid)
    {
        // type, 4 signatures, u16 len + features, chain_hash, short_channel_id
        scid = 0;
        var featuresLengthAt = TypeLength + 4 * SignatureLength;
        if (wire.Length < featuresLengthAt + 2)
            return false;

        var scidAt = featuresLengthAt + 2 + BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(featuresLengthAt))
                   + ChainHashLength;
        if (wire.Length < scidAt + ScidLength)
            return false;

        scid = BinaryPrimitives.ReadUInt64BigEndian(wire.AsSpan(scidAt));
        return true;
    }

    private static bool TryReadNodeId(byte[] wire, out string nodeId)
    {
        // type, signature, u16 flen + features, timestamp, node_id
        nodeId = string.Empty;
        var featuresLengthAt = TypeLength + SignatureLength;
        if (wire.Length < featuresLengthAt + 2)
            return false;

        var nodeIdAt = featuresLengthAt + 2 + BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(featuresLengthAt)) + 4;
        if (wire.Length < nodeIdAt + NodeIdLength)
            return false;

        nodeId = Convert.ToHexStringLower(wire.AsSpan(nodeIdAt, NodeIdLength));
        return true;
    }

    private static bool TryReadUpdate(byte[] wire, out (ulong ShortChannelId, int Direction) update)
    {
        // type, signature, chain_hash, short_channel_id, timestamp, message_flags, channel_flags
        update = default;
        var scidAt = TypeLength + SignatureLength + ChainHashLength;
        var channelFlagsAt = scidAt + ScidLength + 4 + 1;
        if (wire.Length <= channelFlagsAt)
            return false;

        update = (BinaryPrimitives.ReadUInt64BigEndian(wire.AsSpan(scidAt)), wire[channelFlagsAt] & 1);
        return true;
    }
}