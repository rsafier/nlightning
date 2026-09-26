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

    /// <summary>Waits until no scheduled round is running or pending (tests).</summary>
    Task WhenIdleAsync();
}