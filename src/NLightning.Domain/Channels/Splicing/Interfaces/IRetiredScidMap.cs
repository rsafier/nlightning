namespace NLightning.Domain.Channels.Splicing.Interfaces;

using Models;
using ValueObjects;

/// <summary>
/// Short channel ids retired by a splice lock, still resolvable for 72 blocks (splicing plan D12, SP2-0, lane SP2-B,
/// SP2-B-T2). <c>HtlcSwitch.ResolveOutgoingChannel</c> consults it only after the live short channel ids and aliases;
/// the channel it names must still be <c>Open</c>. A singleton, thread-safe; no table of its own: it is rebuilt from
/// the <c>ChannelFundings</c> rows (replaced fundings' short channel ids and the locked splice's confirmation height)
/// by <see cref="LoadAsync"/>.
/// </summary>
public interface IRetiredScidMap
{
    /// <summary>
    /// Records that <paramref name="retired"/> stopped being the channel's short channel id (called after the lock's
    /// save). Registering the same short channel id again keeps the later expiry.
    /// </summary>
    void Retire(RetiredShortChannelId retired);

    /// <summary>
    /// The channel a retired, unexpired short channel id resolves to. False for a short channel id that was never
    /// retired, or that <see cref="PruneExpired"/> removed.
    /// </summary>
    bool TryResolve(ShortChannelId shortChannelId, out ChannelId channelId);

    /// <summary>The retired short channel ids of a channel, oldest first (<c>listchannels</c>, SP2-D).</summary>
    IReadOnlyList<RetiredShortChannelId> GetByChannel(ChannelId channelId);

    /// <summary>Removes every entry whose <see cref="RetiredShortChannelId.ExpiresAtHeight"/> is at or below
    /// <paramref name="height"/> (called on every block); returns how many were removed.</summary>
    int PruneExpired(uint height);

    /// <summary>
    /// Rebuilds the map from the persisted fundings of the channels that are not Closed or Stale, dropping what expired
    /// at <paramref name="currentHeight"/> (host startup, before the peer manager connects).
    /// </summary>
    Task LoadAsync(uint currentHeight, CancellationToken cancellationToken = default);
}