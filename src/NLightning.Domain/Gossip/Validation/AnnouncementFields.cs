namespace NLightning.Domain.Gossip.Validation;

using Channels.ValueObjects;
using Protocol.ValueObjects;

/// <summary>
/// The <c>channel_announcement</c> fields <see cref="GossipValidator"/> checks. Keys are raw 33-byte values so that
/// a malformed key is reported (B7-CA-03) instead of failing to parse.
/// </summary>
public sealed record ChannelAnnouncementFields(
    ChainHash ChainHash,
    ShortChannelId ShortChannelId,
    ReadOnlyMemory<byte> NodeId1,
    ReadOnlyMemory<byte> NodeId2,
    ReadOnlyMemory<byte> BitcoinKey1,
    ReadOnlyMemory<byte> BitcoinKey2,
    ReadOnlyMemory<byte> Features);

/// <summary>
/// The <c>node_announcement</c> fields <see cref="GossipValidator"/> checks.
/// </summary>
/// <param name="NodeId">The raw 33-byte node id.</param>
/// <param name="Timestamp">The announcement's timestamp.</param>
/// <param name="Features">The node features (wire bitmap).</param>
/// <param name="Addresses">The raw <c>addresses</c> field (without the <c>addrlen</c> prefix).</param>
public sealed record NodeAnnouncementFields(
    ReadOnlyMemory<byte> NodeId,
    uint Timestamp,
    ReadOnlyMemory<byte> Features,
    ReadOnlyMemory<byte> Addresses);