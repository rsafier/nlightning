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
/// <param name="Timestamp">The update's timestamp (UNIX seconds by convention).</param>
/// <param name="MessageFlags">The raw <c>message_flags</c>.</param>
/// <param name="ChannelFlags">The raw <c>channel_flags</c> (bit 0 direction, bit 1 disable).</param>
/// <param name="CltvExpiryDelta">The blocks the origin subtracts from an incoming HTLC's <c>cltv_expiry</c>.</param>
/// <param name="HtlcMinimumMsat">The smallest HTLC the channel carries in this direction.</param>
/// <param name="HtlcMaximumMsat">The largest HTLC the origin sends through the channel.</param>
/// <param name="FeeBaseMsat">The base fee.</param>
/// <param name="FeeProportionalMillionths">The proportional fee.</param>
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
                               update.FeeProportionalMillionths);
    }

    /// <summary>
    /// True when every field of <paramref name="update"/> after its <c>timestamp</c> equals this policy's (BOLT 7
    /// compares them for a same-timestamp update). Unknown trailing fields are not compared.
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
            && HtlcMaximumMsat == update.HtlcMaximumMsat;
    }
}