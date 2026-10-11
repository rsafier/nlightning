namespace NLightning.Domain.Gossip.Graph;

using Protocol.Payloads;

/// <summary>
/// One direction of a graph channel: the forwarding policy its origin announced in its latest accepted
/// <c>channel_update</c> (BOLT 7 type 258). Immutable read model.
/// </summary>
/// <remarks>
/// Amounts are raw msat (<c>ulong</c>), like <see cref="ChannelUpdatePayload"/> and the commitment engine: the graph
/// is read on every pathfinding step, and <c>LightningMoney</c> is a mutable reference type.
/// </remarks>
/// <param name="Timestamp">The update's timestamp: UNIX seconds by convention for a <c>channel_update</c>, the block
/// height for a <c>channel_update_2</c> (<see cref="GossipVersion"/> 2).</param>
/// <param name="MessageFlags">The raw <c>message_flags</c>.</param>
/// <param name="ChannelFlags">The raw <c>channel_flags</c> (bit 0 direction, bit 1 disable).</param>
/// <param name="CltvExpiryDelta">The blocks the origin subtracts from an incoming HTLC's <c>cltv_expiry</c>.</param>
/// <param name="HtlcMinimumMsat">The smallest HTLC the channel carries in this direction.</param>
/// <param name="HtlcMaximumMsat">The largest HTLC the origin sends through the channel.</param>
/// <param name="FeeBaseMsat">The base fee.</param>
/// <param name="FeeProportionalMillionths">The proportional fee.</param>
/// <remarks>
/// Equality is by value (NL-354): every field and <see cref="ExtraData"/> by content; <see cref="RawUpdate"/> does not
/// take part (like <c>RawAnnouncement</c> in <see cref="GraphNode"/> and <see cref="GraphChannel"/>), so a policy
/// reloaded from the database equals the one before a restart, and one built by hand equals the parsed one.
/// </remarks>
public sealed record GraphPolicy(
    uint Timestamp,
    byte MessageFlags,
    byte ChannelFlags,
    ushort CltvExpiryDelta,
    ulong HtlcMinimumMsat,
    ulong HtlcMaximumMsat,
    uint FeeBaseMsat,
    uint FeeProportionalMillionths)
{
    /// <summary>
    /// The signed <c>channel_update</c> bytes this policy came from, kept byte-exact for relay and query replies
    /// (empty when unknown, e.g. a policy built for a test or from an invoice hint).
    /// </summary>
    public ReadOnlyMemory<byte> RawUpdate { get; init; }

    /// <summary>
    /// The unknown fields that followed <c>htlc_maximum_msat</c> in the update (covered by its signature; BOLT 7
    /// compares them for a same-timestamp update). Empty when there were none or they are unknown; a store that
    /// rebuilds the policy from <see cref="RawUpdate"/> should parse them back with it.
    /// </summary>
    public ReadOnlyMemory<byte> ExtraData { get; init; }

    /// <summary>
    /// The gossip protocol of the update: 1 for a BOLT 7 <c>channel_update</c> (258), 2 for a taproot gossip
    /// <c>channel_update_2</c> (271, NL-878), whose <see cref="Timestamp"/> is a block height.
    /// </summary>
    public byte GossipVersion { get; init; } = 1;

    /// <summary>
    /// The raw <c>disable_flags</c> of a <c>channel_update_2</c> (0 for a v1 update, whose disable bit lives in
    /// <see cref="ChannelFlags"/>; any v2 flag sets that bit too).
    /// </summary>
    public byte DisableFlags { get; init; }

    /// <summary>
    /// The positive-only inbound base fee of a <c>channel_update_2</c>: the origin's surcharge on an HTLC that arrives
    /// over this channel (0 for a v1 update).
    /// </summary>
    public uint InboundFeeBaseMsat { get; init; }

    /// <summary>The positive-only inbound proportional fee of a <c>channel_update_2</c> (0 for a v1 update).</summary>
    public uint InboundFeeProportionalMillionths { get; init; }

    /// <summary>True when the update charges an inbound fee.</summary>
    public bool HasInboundFee => InboundFeeBaseMsat != 0 || InboundFeeProportionalMillionths != 0;

    /// <summary>True for a <c>channel_update_2</c> (its <see cref="Timestamp"/> is a block height).</summary>
    public bool IsV2 => GossipVersion == 2;

    /// <summary>
    /// The <c>direction</c> bit: 0 when the origin is <c>node_id_1</c>, 1 when it is <c>node_id_2</c>.
    /// </summary>
    public byte Direction => (byte)(ChannelFlags & ChannelUpdatePayload.ChannelFlagDirection);

    /// <summary>
    /// The <c>disable</c> bit.
    /// </summary>
    public bool IsDisabled => (ChannelFlags & ChannelUpdatePayload.ChannelFlagDisable) != 0;

    /// <summary>
    /// The <c>dont_forward</c> bit (an update meant only for the channel peer).
    /// </summary>
    public bool DontForward => (MessageFlags & ChannelUpdatePayload.MessageFlagDontForward) != 0;

    /// <summary>
    /// Builds the policy from a parsed <c>channel_update</c>; <see cref="RawUpdate"/> is left to the caller (the
    /// payload does not carry the message type).
    /// </summary>
    public static GraphPolicy FromChannelUpdate(ChannelUpdatePayload update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return new GraphPolicy(update.Timestamp, update.MessageFlags, update.ChannelFlags, update.CltvExpiryDelta,
                               update.HtlcMinimumMsat, update.HtlcMaximumMsat, update.FeeBaseMsat,
                               update.FeeProportionalMillionths)
        {
            ExtraData = update.ExtraData.IsEmpty ? default : update.ExtraData.ToArray()
        };
    }

    /// <summary>
    /// Builds the policy from a parsed <c>channel_update_2</c> (taproot gossip, NL-878): the block height is the
    /// timestamp, any <c>disable_flags</c> bit sets the disable bit, and an absent <c>htlc_maximum_msat</c> is the
    /// draft's default, half the channel capacity (<paramref name="capacityMsat"/>; 0 when unknown, which makes the
    /// direction unusable). <see cref="RawUpdate"/> is left to the caller.
    /// </summary>
    public static GraphPolicy FromChannelUpdate2(ChannelUpdate2Payload update, ulong? capacityMsat)
    {
        ArgumentNullException.ThrowIfNull(update);
        var channelFlags = (byte)(update.Direction | (update.IsDisabled ? ChannelUpdatePayload.ChannelFlagDisable : 0));
        var htlcMaximum = update.HtlcMaximumMsat ?? (capacityMsat ?? 0) / 2;
        return new GraphPolicy(update.BlockHeight, 0, channelFlags, update.CltvExpiryDelta, update.HtlcMinimumMsat,
                               htlcMaximum, update.FeeBaseMsat, update.FeeProportionalMillionths)
        {
            GossipVersion = 2,
            DisableFlags = update.DisableFlags,
            InboundFeeBaseMsat = update.InboundFeeBaseMsat,
            InboundFeeProportionalMillionths = update.InboundFeeProportionalMillionths
        };
    }

    /// <summary>
    /// True when every field of <paramref name="update"/> after its <c>timestamp</c> equals this policy's (BOLT 7
    /// compares them for a same-timestamp update), including the unknown trailing fields (<see cref="ExtraData"/>).
    /// </summary>
    public bool HasSameFieldsAs(ChannelUpdatePayload update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return MessageFlags == update.MessageFlags
            && ChannelFlags == update.ChannelFlags
            && CltvExpiryDelta == update.CltvExpiryDelta
            && HtlcMinimumMsat == update.HtlcMinimumMsat
            && FeeBaseMsat == update.FeeBaseMsat
            && FeeProportionalMillionths == update.FeeProportionalMillionths
            && HtlcMaximumMsat == update.HtlcMaximumMsat
            && ExtraData.Span.SequenceEqual(update.ExtraData.Span);
    }

    public bool Equals(GraphPolicy? other) =>
        other is not null
     && Timestamp == other.Timestamp
     && MessageFlags == other.MessageFlags
     && ChannelFlags == other.ChannelFlags
     && CltvExpiryDelta == other.CltvExpiryDelta
     && HtlcMinimumMsat == other.HtlcMinimumMsat
     && HtlcMaximumMsat == other.HtlcMaximumMsat
     && FeeBaseMsat == other.FeeBaseMsat
     && FeeProportionalMillionths == other.FeeProportionalMillionths
     && GossipVersion == other.GossipVersion
     && DisableFlags == other.DisableFlags
     && InboundFeeBaseMsat == other.InboundFeeBaseMsat
     && InboundFeeProportionalMillionths == other.InboundFeeProportionalMillionths
     && ExtraData.Span.SequenceEqual(other.ExtraData.Span);

    public override int GetHashCode() =>
        HashCode.Combine(Timestamp, ChannelFlags, CltvExpiryDelta, HtlcMinimumMsat, HtlcMaximumMsat, FeeBaseMsat,
                         FeeProportionalMillionths);
}