namespace NLightning.Domain.Payments.Interception;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Keysend;
using Money;

/// <summary>
/// A forward held for an external interceptor (LND's <c>InterceptedPacket</c>, NL-1183): the incoming HTLC and what
/// its onion asks us to forward, before the outgoing channel is resolved and the forwarding policy runs.
/// </summary>
/// <param name="IncomingChannelId">The incoming channel.</param>
/// <param name="IncomingHtlcId">The peer's id of the incoming HTLC.</param>
/// <param name="IncomingShortChannelId">The incoming channel's short channel id (its alias before it confirms): with
/// <paramref name="IncomingHtlcId"/> the key the interceptor answers with.</param>
/// <param name="OutgoingRequestedShortChannelId">The onion's <c>short_channel_id</c> (default inside a blinded route
/// that names the next node).</param>
/// <param name="OutgoingRequestedNodeId">The next node a blinded route names instead, if any.</param>
/// <param name="PaymentHash">The payment hash.</param>
/// <param name="IncomingAmount">The incoming HTLC's amount.</param>
/// <param name="OutgoingAmount">The amount the onion asks us to forward.</param>
/// <param name="IncomingExpiry">The incoming HTLC's <c>cltv_expiry</c>.</param>
/// <param name="OutgoingExpiry">The <c>outgoing_cltv_value</c> the onion asks for.</param>
/// <param name="AutoFailHeight">The block height at which the held forward is failed back.</param>
/// <param name="NextOnion">The onion for the next hop.</param>
/// <param name="CustomRecords">The hop payload's records of type 65536 or more.</param>
public sealed record InterceptedForward(
    ChannelId IncomingChannelId,
    ulong IncomingHtlcId,
    ShortChannelId IncomingShortChannelId,
    ShortChannelId OutgoingRequestedShortChannelId,
    CompactPubKey? OutgoingRequestedNodeId,
    Hash PaymentHash,
    LightningMoney IncomingAmount,
    LightningMoney OutgoingAmount,
    uint IncomingExpiry,
    uint OutgoingExpiry,
    uint AutoFailHeight,
    ReadOnlyMemory<byte> NextOnion,
    IReadOnlyList<CustomRecord> CustomRecords);