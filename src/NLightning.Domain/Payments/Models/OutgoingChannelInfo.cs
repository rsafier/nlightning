namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Money;

/// <summary>
/// A snapshot of the outgoing channel of a forward, taken without holding its lock (the forward is re-checked by the
/// channel engine when the HTLC is actually offered).
/// </summary>
/// <param name="ChannelId">The outgoing channel.</param>
/// <param name="IsUsable">True when it is <c>Open</c>, its peer is connected and <c>channel_reestablish</c> was
/// processed, and it is neither failed nor shutting down. False yields <c>temporary_channel_failure</c> (BOLT 4
/// <c>channel_disabled</c> needs a disabled <c>channel_update</c>, which we do not announce yet).</param>
/// <param name="HtlcMinimum">The peer's <c>htlc_minimum_msat</c> for HTLCs we offer on this channel.</param>
/// <param name="AvailableToSend">What we can still offer on this channel now (balance minus reserve, fees and
/// in-flight limits). An outgoing amount above it yields <c>temporary_channel_failure</c>.</param>
public sealed record OutgoingChannelInfo(
    ChannelId ChannelId,
    bool IsUsable,
    LightningMoney HtlcMinimum,
    LightningMoney AvailableToSend);