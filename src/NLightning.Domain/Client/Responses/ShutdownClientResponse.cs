namespace NLightning.Domain.Client.Responses;

using Enums;

/// <summary>
/// The answer to <c>shutdown</c> (<c>ClientCommand.Shutdown</c>, NL-591/NL-592): what happened, with the counts.
/// </summary>
/// <remarks>
/// The first pass (NL-591) only stopped (no HTLCs in flight) or threw; the second pass's additions carry defaults, so
/// an older response stays a valid first-pass one.
/// </remarks>
public sealed class ShutdownClientResponse
{
    public ShutdownClientResponse(int channelCount)
    {
        ChannelCount = channelCount;
    }

    /// <summary>The channels that are not Closed or Stale; they reestablish when the node starts again.</summary>
    public int ChannelCount { get; }

    /// <summary>How the shutdown ended; a refusal is an error envelope instead (NL-592).</summary>
    public ShutdownOutcome Outcome { get; init; } = ShutdownOutcome.Stopped;

    /// <summary>
    /// The HTLCs in flight when the node stopped (<see cref="ShutdownOutcome.Forced"/>) or when the wait timed out
    /// (<see cref="ShutdownOutcome.TimedOut"/>); zero for <see cref="ShutdownOutcome.Stopped"/>.
    /// </summary>
    public int HtlcsInFlight { get; init; }

    /// <summary>
    /// The HTLCs of channels that resolve on chain (Failed, OnchainResolving) at the same moments, in every outcome:
    /// they never keep the node from stopping, and the on-chain resolvers take them up at the next start (NL-1006).
    /// </summary>
    public int HtlcsResolvingOnChain { get; init; }

    /// <summary>The negotiations (splices, RBFs, opens, quiescences) still running at the same moments.</summary>
    public int NegotiationCount { get; init; }

    /// <summary>The channels the <see cref="HtlcsInFlight"/> and <see cref="NegotiationCount"/> come from.</summary>
    public IReadOnlyList<ShutdownBusyChannel> BusyChannels { get; init; } = [];

    /// <summary>
    /// The nearest <c>cltv_expiry</c> among the in-flight HTLCs (<see cref="ShutdownOutcome.Forced"/>, NL-592, or any
    /// outcome with <see cref="HtlcsResolvingOnChain"/>, NL-1006); 0 when none was in flight. Stopping is safe for
    /// funds while the node is back before the HTLC deadlines: the expiry monitor and BOLT 5 resolvers run at the start.
    /// </summary>
    public uint NearestCltvExpiry { get; init; }

    /// <summary>
    /// The blocks that remain until our deadline to act on the nearest-deadline HTLC
    /// (<see cref="Domain.Channels.Policies.HtlcDeadlinePolicy"/>'s rule), -1 when the height is unknown and 0 when
    /// the deadline is this block.
    /// </summary>
    public int BlocksUntilDeadline { get; init; } = -1;
}