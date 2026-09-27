namespace NLightning.Application.Payments.Routing;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;

/// <summary>
/// One layer of a payment onion: the node that peels it and what its payload tells it to do.
/// </summary>
/// <param name="NodeId">The node that peels this layer (inside a blinded path: the introduction node's real id, then
/// the blinded node ids).</param>
/// <param name="AmountToForward"><c>amt_to_forward</c>: the amount of the HTLC this node offers next (or, at the
/// final node, the amount it receives). Inside a blinded path, where only the final payload carries it, the hops
/// before the final one hold the final amount for bookkeeping.</param>
/// <param name="OutgoingCltvValue"><c>outgoing_cltv_value</c>: the <c>cltv_expiry</c> of that HTLC (bookkeeping only
/// before the final hop of a blinded path, like the amount).</param>
/// <param name="OutgoingShortChannelId"><c>short_channel_id</c> of the channel this node forwards on; null for the
/// final node and for the hops of a blinded path.</param>
public sealed record RouteHop(
    CompactPubKey NodeId,
    LightningMoney AmountToForward,
    uint OutgoingCltvValue,
    ShortChannelId? OutgoingShortChannelId)
{
    /// <summary>
    /// The hop's <c>encrypted_recipient_data</c> when it is a hop of a blinded path (BOLT 4 "Route Blinding"); null
    /// outside one.
    /// </summary>
    public ReadOnlyMemory<byte>? EncryptedRecipientData { get; init; }

    /// <summary>
    /// <c>current_path_key</c>: the blinded path's first path_key, given to its introduction node only.
    /// </summary>
    public CompactPubKey? CurrentPathKey { get; init; }

    /// <summary>
    /// True for a hop of a blinded path that relays further (its next channel is hidden in its
    /// <see cref="EncryptedRecipientData"/>, so it has no <see cref="OutgoingShortChannelId"/>).
    /// </summary>
    public bool IsBlindedRelay { get; init; }

    public bool IsBlinded => EncryptedRecipientData is not null;

    public bool IsFinal => OutgoingShortChannelId is null && !IsBlindedRelay;
}