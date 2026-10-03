namespace NLightning.Domain.Channels.ValueObjects;

using Crypto.ValueObjects;

/// <summary>
/// One side's half of a <c>channel_announcement</c> (BOLT 7 <c>announcement_signatures</c>): the node signature and the
/// bitcoin (funding key) signature over the announcement's double-SHA256.
/// </summary>
/// <param name="NodeSignature">The signature with the node key (<c>node_signature</c>).</param>
/// <param name="BitcoinSignature">The signature with the channel's funding key (<c>bitcoin_signature</c>).</param>
public sealed record ChannelAnnouncementSignatures(CompactSignature NodeSignature, CompactSignature BitcoinSignature);