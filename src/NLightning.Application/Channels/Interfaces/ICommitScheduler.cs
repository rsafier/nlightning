namespace NLightning.Application.Channels.Interfaces;

using Domain.Channels.ValueObjects;

/// <summary>
/// Sends our <c>commitment_signed</c> for updates we made (BOLT2 plan N6-T2, §3.4 "Batching"): debounced, only when
/// the peer looks alive (ping-before-commit), and never while a signed commitment waits for its
/// <c>revoke_and_ack</c> (decision D7: one outstanding <c>commitment_signed</c> per direction).
/// </summary>
/// <remarks>
/// The receive handlers sign on their own after <c>commitment_signed</c>/<c>revoke_and_ack</c> when changes are
/// pending; the scheduler covers updates made by <c>IChannelOperations</c>, which have no peer message to answer.
/// </remarks>
public interface ICommitScheduler
{
    /// <summary>
    /// Asks for a <c>commitment_signed</c> on <paramref name="channelId"/> after the debounce delay. Several requests
    /// within the delay become one signature. Never blocks and never throws; call it after releasing the lock.
    /// </summary>
    void Schedule(ChannelId channelId);

    /// <summary>
    /// Signs now if the channel has pending changes, no unacknowledged commitment and a live peer: takes the channel's
    /// lock, persists the signed commitment with its diff, then enqueues the <c>commitment_signed</c>.
    /// </summary>
    /// <returns>True when a <c>commitment_signed</c> was sent.</returns>
    Task<bool> SignNowAsync(ChannelId channelId, CancellationToken cancellationToken = default);

    /// <summary>Completes when no scheduled signature is waiting or running (tests and shutdown).</summary>
    Task WhenIdleAsync();
}