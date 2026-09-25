namespace NLightning.Application.Payments.Onion;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// The onion names a next hop: forward <see cref="NextPacket"/> on the channel of
/// <see cref="OutgoingShortChannelId"/> with <see cref="AmountToForward"/> and <see cref="OutgoingCltvValue"/>, once
/// <c>IForwardingPolicy</c> agrees.
/// </summary>
/// <param name="SharedSecret">The shared secret with the origin: it creates or wraps any failure of this HTLC.</param>
/// <param name="Payload">The validated hop payload (non-blinded, with <c>short_channel_id</c>, <c>amt_to_forward</c>
/// and <c>outgoing_cltv_value</c>).</param>
/// <param name="NextPacket">The packet for the next hop (<c>update_add_htlc.onion_routing_packet</c>).</param>
public sealed record IncomingOnionForward(Secret SharedSecret, HopPayload Payload, OnionPacket NextPacket)
    : IncomingOnionResult
{
    /// <inheritdoc />
    public override Secret? SharedSecretOrNull => SharedSecret;

    /// <summary>
    /// The onion's <c>short_channel_id</c> (real or alias) of the outgoing channel.
    /// </summary>
    public ShortChannelId OutgoingShortChannelId =>
        Payload.ShortChannelId ?? throw new InvalidOperationException("A forward payload has a short_channel_id.");

    /// <summary>
    /// The onion's <c>amt_to_forward</c>: the amount of the outgoing HTLC.
    /// </summary>
    public LightningMoney AmountToForward =>
        Payload.AmtToForward ?? throw new InvalidOperationException("A forward payload has amt_to_forward.");

    /// <summary>
    /// The onion's <c>outgoing_cltv_value</c>: the <c>cltv_expiry</c> of the outgoing HTLC.
    /// </summary>
    public uint OutgoingCltvValue =>
        Payload.OutgoingCltvValue
     ?? throw new InvalidOperationException("A forward payload has outgoing_cltv_value.");
}