namespace NLightning.Domain.Channels.Interfaces;

using ValueObjects;

/// <summary>
/// Hands out one exclusive, non-reentrant lock per channel id (BOLT2 plan D2, §3.7).
/// </summary>
/// <remarks>
/// Hold the lock around every read-modify-persist-send of a channel: peer messages, block events, IPC operations and
/// reestablish. Keep it until the transition's outbound messages are enqueued, so wire order equals persist order.
/// The lock is not reentrant: never acquire it again while holding it, and never hold two channel locks at once.
/// </remarks>
public interface IChannelLockProvider
{
    /// <summary>
    /// Waits for the channel's lock. Dispose the result to release it.
    /// </summary>
    Task<IDisposable> AcquireAsync(ChannelId channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocking variant of <see cref="AcquireAsync"/> for synchronous event handlers. Dispose the result to release it.
    /// </summary>
    IDisposable Acquire(ChannelId channelId);
}