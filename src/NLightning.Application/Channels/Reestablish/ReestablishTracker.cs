using System.Collections.Concurrent;

namespace NLightning.Application.Channels.Reestablish;

using Domain.Channels.Reestablish;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The in-memory reestablish state of every channel on its peer's current connection (BOLT2 plan N7-T2).
/// </summary>
/// <remarks>
/// <para>
/// A channel is <see cref="ReestablishStatus.Awaiting"/> unless recorded otherwise. <see cref="MarkSent"/> records our
/// <c>channel_reestablish</c> on the current connection; only such a channel can become
/// <see cref="ReestablishStatus.Reestablished"/> through <see cref="TryMarkReestablished"/> (a compare-and-set), so a
/// reestablish that finishes after the connection was replaced (<see cref="ResetPeer"/>) never counts for the new one.
/// </para>
/// <para>
/// A channel that turns Open on the current connection is marked with <see cref="MarkOpened"/>, a separate flag: it can
/// carry updates (<see cref="IsReestablished"/>) but its reestablish status is kept. A peer may still send its
/// <c>channel_reestablish</c> after channel_ready on that connection (LND does for a channel that was pending when the
/// connection started, and waits for ours forever), so the handler still answers it when ours did not go out.
/// </para>
/// <para>
/// <see cref="ResetPeer"/> runs synchronously when the peer's connection is replaced or drops, before the new
/// connection can carry anything, so a stale "reestablished" never covers a new connection.
/// </para>
/// </remarks>
public sealed class ReestablishTracker : IReestablishTracker
{
    private readonly ConcurrentDictionary<ChannelId, Entry> _channels = new();

    /// <inheritdoc />
    public bool IsReestablished(ChannelId channelId) =>
        _channels.TryGetValue(channelId, out var entry)
     && (entry.Status == ReestablishStatus.Reestablished || entry.OpenedHere);

    /// <summary>
    /// The channel's <c>channel_reestablish</c> exchange on the peer's current connection (independent of
    /// <see cref="MarkOpened"/>).
    /// </summary>
    public ReestablishStatus GetStatus(ChannelId channelId) =>
        _channels.TryGetValue(channelId, out var entry) ? entry.Status : ReestablishStatus.Awaiting;

    /// <summary>Records that our <c>channel_reestablish</c> went out on the peer's current connection.</summary>
    public void MarkSent(ChannelId channelId, CompactPubKey peerPubKey) =>
        _channels.AddOrUpdate(channelId, _ => new Entry(peerPubKey, ReestablishStatus.Sent, false),
                              (_, entry) => new Entry(peerPubKey, ReestablishStatus.Sent,
                                                      entry.PeerPubKey == peerPubKey && entry.OpenedHere));

    /// <summary>
    /// Marks the channel reestablished if our <c>channel_reestablish</c> went out on the current connection (and
    /// nothing reset it since).
    /// </summary>
    /// <returns>False when the connection was replaced or dropped meanwhile.</returns>
    public bool TryMarkReestablished(ChannelId channelId)
    {
        if (!_channels.TryGetValue(channelId, out var entry) || entry.Status != ReestablishStatus.Sent)
            return false;

        return _channels.TryUpdate(channelId, entry with { Status = ReestablishStatus.Reestablished }, entry);
    }

    /// <summary>
    /// A channel that turned Open on the peer's current connection can carry updates on it (channel_ready starts
    /// normal operation). Its reestablish status is kept: a <c>channel_reestablish</c> the peer still sends on this
    /// connection is answered with ours if ours did not go out yet.
    /// </summary>
    public void MarkOpened(ChannelId channelId, CompactPubKey peerPubKey) =>
        _channels.AddOrUpdate(channelId, _ => new Entry(peerPubKey, ReestablishStatus.Awaiting, true),
                              (_, entry) => entry.PeerPubKey == peerPubKey
                                                ? entry with { OpenedHere = true }
                                                : new Entry(peerPubKey, ReestablishStatus.Awaiting, true));

    /// <summary>Forgets the channel's state (it goes back to <see cref="ReestablishStatus.Awaiting"/>).</summary>
    public void Reset(ChannelId channelId) => _channels.TryRemove(channelId, out _);

    /// <summary>
    /// Forgets every channel of <paramref name="peerPubKey"/>: its connection dropped or was replaced.
    /// </summary>
    public void ResetPeer(CompactPubKey peerPubKey)
    {
        foreach (var pair in _channels)
            if (pair.Value.PeerPubKey == peerPubKey)
                _channels.TryRemove(pair);
    }

    private sealed record Entry(CompactPubKey PeerPubKey, ReestablishStatus Status, bool OpenedHere);
}

/// <summary>A channel's reestablish status on its peer's current connection.</summary>
public enum ReestablishStatus
{
    /// <summary>Nothing was sent on this connection yet.</summary>
    Awaiting,

    /// <summary>Our <c>channel_reestablish</c> went out; waiting for the peer's.</summary>
    Sent,

    /// <summary>Both were exchanged: updates may flow, and a further <c>channel_reestablish</c> is ignored.</summary>
    Reestablished
}