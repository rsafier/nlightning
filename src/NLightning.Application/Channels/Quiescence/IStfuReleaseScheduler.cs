namespace NLightning.Application.Channels.Quiescence;

using Domain.Channels.ValueObjects;

/// <summary>
/// Sends an owed <c>stfu</c> once BOLT 2 allows it (Q-S-02), after the commitment transition that made it possible has
/// gone out on the wire.
/// </summary>
/// <remarks>
/// <para>Called by <c>ChannelStateTransitionService</c> after every persisted transition, under the channel's lock. The
/// <c>stfu</c> can't be published right there: the transition's own reply (our <c>revoke_and_ack</c> after a
/// <c>commitment_signed</c>) is returned by the handler and raised only after it, and a <c>stfu</c> ahead of that
/// <c>revoke_and_ack</c> would tell the peer that our changes are committed while, from its side, they are still
/// pending. So the release runs on the thread pool: it takes the channel's lock once the handler's replies were
/// raised, calls <see cref="Domain.Channels.Quiescence.IQuiescenceService.TryReleaseStfu"/> and publishes the result,
/// which puts the <c>stfu</c> behind them in the peer's outbox.</para>
/// <para>Returns at once; does nothing when no <c>stfu</c> is owed on the channel.</para>
/// </remarks>
public interface IStfuReleaseScheduler
{
    /// <summary>Schedules the release of the <c>stfu</c> owed on <paramref name="channelId"/>, if any.</summary>
    void ScheduleRelease(ChannelId channelId);
}