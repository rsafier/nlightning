namespace NLightning.Domain.Gossip.Persistence;

using Channels.ValueObjects;

/// <summary>
/// The latest accepted <c>channel_update</c> of one direction of a graph channel (BOLT 7, table
/// <c>GraphChannelPolicies</c>, primary key short channel id + direction): its fields and the raw signed bytes. A
/// policy belongs to a stored <see cref="GraphChannelRecord"/> and is deleted with it.
/// </summary>
/// <remarks>The byte array is held as given (not copied), and record equality compares it by reference.</remarks>
/// <param name="ShortChannelId">The channel.</param>
/// <param name="Direction">
/// The <c>direction</c> bit of <c>channel_flags</c>: 0 = sent by <c>node_id_1</c>, 1 = by <c>node_id_2</c>.
/// </param>
/// <param name="Timestamp">The update's <c>timestamp</c>.</param>
/// <param name="MessageFlags">The raw <c>message_flags</c>.</param>
/// <param name="ChannelFlags">The raw <c>channel_flags</c> (direction and disable bits).</param>
/// <param name="CltvExpiryDelta">The <c>cltv_expiry_delta</c>.</param>
/// <param name="HtlcMinimumMsat">The <c>htlc_minimum_msat</c>.</param>
/// <param name="HtlcMaximumMsat">The <c>htlc_maximum_msat</c>.</param>
/// <param name="FeeBaseMsat">The <c>fee_base_msat</c>.</param>
/// <param name="FeeProportionalMillionths">The <c>fee_proportional_millionths</c>.</param>
/// <param name="RawUpdate">The whole <c>channel_update</c> payload (signature included, the message type excluded).</param>
public sealed record GraphPolicyRecord(
    ShortChannelId ShortChannelId,
    byte Direction,
    uint Timestamp,
    byte MessageFlags,
    byte ChannelFlags,
    ushort CltvExpiryDelta,
    ulong HtlcMinimumMsat,
    ulong HtlcMaximumMsat,
    uint FeeBaseMsat,
    uint FeeProportionalMillionths,
    byte[] RawUpdate);