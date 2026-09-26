namespace NLightning.Application.Payments.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// One layer of a payment onion: the node that peels it and what its payload tells it to do.
/// </summary>
/// <param name="NodeId">The node that peels this layer.</param>
/// <param name="AmountToForward"><c>amt_to_forward</c>: the amount of the HTLC this node offers next (or, at the
/// final node, the amount it receives).</param>
/// <param name="OutgoingCltvValue"><c>outgoing_cltv_value</c>: the <c>cltv_expiry</c> of that HTLC.</param>
/// <param name="OutgoingShortChannelId"><c>short_channel_id</c> of the channel this node forwards on; null for the
/// final node.</param>
public sealed record RouteHop(
    CompactPubKey NodeId,
    LightningMoney AmountToForward,
    uint OutgoingCltvValue,
    ShortChannelId? OutgoingShortChannelId)
{
    public bool IsFinal => OutgoingShortChannelId is null;
}