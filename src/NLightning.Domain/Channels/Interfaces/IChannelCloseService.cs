namespace NLightning.Domain.Channels.Interfaces;

using Bitcoin.ValueObjects;
using Enums;
using ValueObjects;

/// <summary>What a mutual close is asked to do (IPC <c>closechannel</c>).</summary>
/// <param name="FeeRatePerKw">The feerate our closing fee estimate uses, or null for the fee service's.</param>
/// <param name="SendFeeRange">Send <c>fee_range</c> with our <c>closing_signed</c> (BOLT 2 SHOULD; off to use the
/// legacy "strictly between" negotiation).</param>
/// <param name="WaitFor">How long to wait for the closing transaction to be agreed and broadcast (null or zero: return
/// once our <c>shutdown</c> is out).</param>
public sealed record ChannelCloseRequest(uint? FeeRatePerKw = null, bool SendFeeRange = true,
                                         TimeSpan? WaitFor = null);

/// <summary>Where a mutual close stands.</summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="State">Its state now (<see cref="ChannelState.ShuttingDown"/>, <see cref="ChannelState.Negotiating"/>,
/// <see cref="ChannelState.Closing"/> once the closing transaction is broadcast, or <see cref="ChannelState.Closed"/>).</param>
/// <param name="ClosingTxId">The txid of the agreed closing transaction, once there is one.</param>
public sealed record ChannelCloseResult(ChannelId ChannelId, ChannelState State, TxId? ClosingTxId);

/// <summary>
/// Starts the cooperative close of a channel (BOLT 2 "Channel Close", legacy <c>closing_signed</c>; BOLT2 plan N10-T3).
/// </summary>
/// <remarks>
/// It takes the channel's lock (callers hold none), signs our pending updates first if needed (a <c>shutdown</c> must
/// not follow unsigned updates), persists our <c>shutdown</c> script and <see cref="ChannelState.ShuttingDown"/>, then
/// sends <c>shutdown</c>. The rest runs on the peer's messages: once no HTLC is left the funder proposes a fee, the
/// agreed closing transaction is persisted, broadcast and watched, and its confirmation makes the channel
/// <see cref="ChannelState.Closed"/>. Calling it again on a closing channel only reports (and waits).
/// </remarks>
public interface IChannelCloseService
{
    /// <exception cref="KeyNotFoundException">The channel is not loaded.</exception>
    /// <exception cref="InvalidOperationException">The channel can't be closed now (not open, failed, the peer is not
    /// connected on the channel's link, or updates are waiting for a signature that can't be sent yet).</exception>
    Task<ChannelCloseResult> CloseChannelAsync(ChannelId channelId, ChannelCloseRequest request,
                                               CancellationToken cancellationToken = default);
}