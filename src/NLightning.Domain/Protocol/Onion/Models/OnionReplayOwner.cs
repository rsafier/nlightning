namespace NLightning.Domain.Protocol.Onion.Models;

using Channels.ValueObjects;

/// <summary>
/// The incoming HTLC an onion arrived with, as the replay set records it (NL-078). The HTLC that records an HMAC first
/// owns it, so re-processing that same HTLC (restart, link-up) is not a replay; the entry is kept until the chain
/// passes <see cref="CltvExpiry"/>.
/// </summary>
/// <param name="ChannelId">The incoming channel.</param>
/// <param name="HtlcId">The incoming HTLC's id on <paramref name="ChannelId"/>.</param>
/// <param name="CltvExpiry">The incoming HTLC's <c>cltv_expiry</c>.</param>
public readonly record struct OnionReplayOwner(ChannelId ChannelId, ulong HtlcId, uint CltvExpiry);