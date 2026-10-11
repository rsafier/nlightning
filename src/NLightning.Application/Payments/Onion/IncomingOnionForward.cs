namespace NLightning.Application.Payments.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// The onion names a next hop: forward <see cref="NextPacket"/> on the channel of
/// <see cref="OutgoingShortChannelId"/> (or, inside a blinded route, to <see cref="NextNodeId"/>) with
/// <see cref="AmountToForward"/> and <see cref="OutgoingCltvValue"/>, once <c>IForwardingPolicy</c> agrees.
/// </summary>
/// <param name="SharedSecret">The shared secret with the origin: it creates or wraps any failure of this HTLC.</param>
/// <param name="Payload">The validated hop payload (non-blinded: <c>short_channel_id</c>, <c>amt_to_forward</c> and
/// <c>outgoing_cltv_value</c>; blinded: only <c>encrypted_recipient_data</c> and <c>current_path_key</c>).</param>
/// <param name="NextPacket">The packet for the next hop (<c>update_add_htlc.onion_routing_packet</c>).</param>
/// <param name="Blinded">Set inside a blinded route (ONION M5): the next hop, amount and expiry come from the
/// recipient's data, and the outgoing <c>update_add_htlc</c> carries <see cref="IncomingBlindedHop.NextPathKey"/>.
/// </param>
public sealed record IncomingOnionForward(Secret SharedSecret, HopPayload Payload, OnionPacket NextPacket,
                                          IncomingBlindedHop? Blinded = null)
    : IncomingOnionResult
{
    /// <inheritdoc />
    public override Secret? SharedSecretOrNull => SharedSecret;

    /// <summary>
    /// Whether the outgoing channel is named by <see cref="NextNodeId"/> instead of a <c>short_channel_id</c>.
    /// </summary>
    public bool HasOutgoingShortChannelId =>
        Blinded is { } blinded ? blinded.RecipientData.ShortChannelId is not null : Payload.ShortChannelId is not null;

    /// <summary>
    /// The <c>short_channel_id</c> (real or alias) of the outgoing channel: the onion's, or inside a blinded route the
    /// recipient data's.
    /// </summary>
    public ShortChannelId OutgoingShortChannelId =>
        (Blinded is { } blinded ? blinded.RecipientData.ShortChannelId : Payload.ShortChannelId)
     ?? throw new InvalidOperationException("The forward names no short_channel_id.");

    /// <summary>
    /// Inside a blinded route, the recipient data's <c>next_node_id</c> when it names no <c>short_channel_id</c>.
    /// </summary>
    public CompactPubKey? NextNodeId => Blinded?.RecipientData.NextNodeId;

    /// <summary>
    /// The amount of the outgoing HTLC: the onion's <c>amt_to_forward</c>, or inside a blinded route the one computed
    /// from <c>payment_relay</c>.
    /// </summary>
    public LightningMoney AmountToForward =>
        (Blinded is { } blinded ? blinded.AmountToForward : Payload.AmtToForward)
     ?? throw new InvalidOperationException("The forward has no amt_to_forward.");

    /// <summary>
    /// The <c>cltv_expiry</c> of the outgoing HTLC: the onion's <c>outgoing_cltv_value</c>, or inside a blinded route
    /// the incoming expiry minus <c>payment_relay.cltv_expiry_delta</c>.
    /// </summary>
    public uint OutgoingCltvValue =>
        (Blinded is { } blinded ? blinded.OutgoingCltvValue : Payload.OutgoingCltvValue)
     ?? throw new InvalidOperationException("The forward has no outgoing_cltv_value.");
}