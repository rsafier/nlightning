namespace NLightning.Domain.Gossip.Persistence;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Graph;

/// <summary>
/// A stored <c>channel_announcement</c> (BOLT 7, table <c>GraphChannels</c>): its parsed fields, the capacity from the
/// funding output, how it was verified, the height its funding output was spent at (if it was), and the raw signed
/// bytes, which relay and query replies forward byte-exact.
/// </summary>
/// <remarks>The byte arrays are held as given (not copied), and record equality compares them by reference.</remarks>
/// <param name="ShortChannelId">The announced short channel id (primary key).</param>
/// <param name="NodeId1">The lesser node id.</param>
/// <param name="NodeId2">The greater node id.</param>
/// <param name="BitcoinKey1">The funding key of <paramref name="NodeId1"/> (null for a <c>channel_announcement_2</c>
/// without bitcoin keys, NL-878).</param>
/// <param name="BitcoinKey2">The funding key of <paramref name="NodeId2"/> (null like <paramref name="BitcoinKey1"/>).
/// </param>
/// <param name="CapacitySat">The funding output's amount.</param>
/// <param name="Features">The raw <c>features</c> bytes (big-endian wire order).</param>
/// <param name="RawAnnouncement">
/// The whole <c>channel_announcement</c> payload (signatures included, the 2-byte message type excluded); empty for a
/// channel announced with <c>channel_announcement_2</c> only.
/// </param>
/// <param name="Verification">How the funding output was checked.</param>
/// <param name="SpentAtHeight">The height of the block that spent the funding output, or null while unspent.</param>
/// <param name="ReceivedAt">When we received (or created) the announcement.</param>
/// <param name="FundingTxId">
/// The funding transaction id, when known (from the chain check, our own channel or the pruner's lookup; NL-352).
/// </param>
/// <param name="Versions">The protocols the channel was announced with (migration <c>AddGossipV2</c>, NL-878).</param>
/// <param name="RawAnnouncement2">The <c>channel_announcement_2</c> payload, or null without one.</param>
public sealed record GraphChannelRecord(
    ShortChannelId ShortChannelId,
    CompactPubKey NodeId1,
    CompactPubKey NodeId2,
    CompactPubKey? BitcoinKey1,
    CompactPubKey? BitcoinKey2,
    ulong CapacitySat,
    byte[] Features,
    byte[] RawAnnouncement,
    GraphChannelVerification Verification,
    uint? SpentAtHeight,
    DateTimeOffset ReceivedAt,
    TxId? FundingTxId = null,
    GraphGossipVersions Versions = GraphGossipVersions.V1,
    byte[]? RawAnnouncement2 = null);