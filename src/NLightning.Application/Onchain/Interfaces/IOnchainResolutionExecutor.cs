namespace NLightning.Application.Onchain.Interfaces;

using Domain.Bitcoin.Events;
using Domain.Channels.ValueObjects;

/// <summary>
/// Runs the on-chain resolution of every channel in <c>OnchainResolving</c> (BOLT 5 plan §3.2 steps 4-5): each block it
/// asks the <see cref="Domain.Onchain.Interfaces.IOutputResolver"/> of the channel's close kind for actions, persists
/// them in one save per channel, then publishes, watches and raises; it applies the 100-block irrevocable rule (O6-T2)
/// and closes the channel when everything is irrevocably resolved.
/// </summary>
public interface IOnchainResolutionExecutor
{
    /// <summary>Schedules a round at <paramref name="height"/> in the background (coalesced to the latest height).</summary>
    void ScheduleRound(uint height);

    /// <summary>Runs one round at <paramref name="height"/> over every channel in <c>OnchainResolving</c>.</summary>
    Task RunRoundAsync(uint height, CancellationToken cancellationToken = default);

    /// <summary>Runs one channel's round at <paramref name="height"/> (after its funding spend was classified).</summary>
    Task ResolveChannelAsync(ChannelId channelId, uint height, CancellationToken cancellationToken = default);

    /// <summary>
    /// A watched resolution output was spent in a processed block: marks it resolved and asks the resolver. Takes the
    /// channel's lock itself. Idempotent.
    /// </summary>
    Task HandleOutputSpentAsync(OutpointSpentEventArgs args, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles the spends of just-tracked resolution-output watches that are already mined, from
    /// <paramref name="fromHeight"/> (their parent's height) up to bitcoind's tip: a block the chain monitor processed
    /// before a watch was tracked never raises its spend. Takes the channel's lock itself; the caller must hold none.
    /// Idempotent; does nothing without an <c>IBitcoinChainService</c>.
    /// </summary>
    Task CatchUpSpendsAsync(ChannelId channelId, IReadOnlyList<Domain.Onchain.Models.WatchedOutpointModel> watches,
                            uint fromHeight, CancellationToken cancellationToken = default);

    /// <summary>
    /// NL-311: catches up the saved watch of every unresolved output of the channel from its parent's height (or from
    /// the spend the chain monitor recorded on it) up to bitcoind's tip. A crash between a save and
    /// <c>TrackWatchedOutpoint</c> leaves watches the monitor tracks again after the restart, but it never rescans the
    /// blocks it processed in between. <see cref="RunRoundAsync"/> runs it once per channel and process, after the
    /// round's resolution of every channel, in one block scan for all channels not caught up yet (a scan stopped by a
    /// chain read error resumes at the first block it did not scan). Takes the channel's lock itself; the caller must
    /// hold none. Idempotent.
    /// </summary>
    Task CatchUpSavedWatchesAsync(ChannelId channelId, CancellationToken cancellationToken = default);

    /// <summary>Waits until no scheduled round is running or pending (tests).</summary>
    Task WhenIdleAsync();
}