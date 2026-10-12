namespace NLightning.Domain.Payments.Models;

using Money;
using Protocol.Onion.Models;

/// <summary>
/// Everything <c>IForwardingPolicy</c> needs to decide whether to forward a locked-in incoming HTLC whose onion names a
/// next hop: the incoming HTLC, what the onion asks for, the current height and the outgoing channel as it is now.
/// </summary>
/// <param name="IncomingAmount">The incoming HTLC's <c>amount_msat</c>.</param>
/// <param name="IncomingCltvExpiry">The incoming HTLC's <c>cltv_expiry</c>.</param>
/// <param name="AmountToForward">The onion's <c>amt_to_forward</c>.</param>
/// <param name="OutgoingCltvValue">The onion's <c>outgoing_cltv_value</c>.</param>
/// <param name="CurrentBlockHeight">Our current chain tip.</param>
/// <param name="OutgoingChannel">The channel the onion's <c>short_channel_id</c> resolves to, or null when it is
/// unknown (<c>unknown_next_peer</c>).</param>
/// <param name="BlindedRelay">Inside a blinded route (ONION M5), the recipient's <c>payment_relay</c> for us:
/// <see cref="AmountToForward"/> and <see cref="OutgoingCltvValue"/> were computed from it, so the fee check compares
/// it with our policy instead of the amounts (its rounding may leave the amounts a msat short of our fee).</param>
public sealed record ForwardingRequest(
    LightningMoney IncomingAmount,
    uint IncomingCltvExpiry,
    LightningMoney AmountToForward,
    uint OutgoingCltvValue,
    uint CurrentBlockHeight,
    OutgoingChannelInfo? OutgoingChannel,
    BlindedPaymentRelay? BlindedRelay = null);