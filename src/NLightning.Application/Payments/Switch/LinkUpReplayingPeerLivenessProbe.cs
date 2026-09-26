namespace NLightning.Application.Payments.Switch;

using Channels.Interfaces;
using Channels.Reestablish;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Decorates the registered <see cref="IPeerLivenessProbe"/> so that marking a channel's link up also replays the
/// channel's pending HTLC events into the switch (<see cref="LinkUpEventReplayer"/>). Registered by
/// <see cref="HtlcSwitchServiceCollectionExtensions.AddHtlcSwitchServices"/>.
/// </summary>
/// <remarks>
/// A link marked up right after a <c>channel_reestablish</c> (the <see cref="ReestablishTracker"/> says
/// <see cref="ReestablishStatus.Reestablished"/>) is not replayed here: <c>ChannelManager</c> queues that channel's pending
/// events itself and hands them to the switch after the lock, so a replay here would do the same work twice (NL-264).
/// Links marked up when a channel turns Open, or by a host without a tracker, are replayed here as before.
/// </remarks>
public sealed class LinkUpReplayingPeerLivenessProbe : IPeerLivenessProbe
{
    private readonly IPeerLivenessProbe _inner;
    private readonly LinkUpEventReplayer _replayer;
    private readonly ReestablishTracker? _tracker;

    public LinkUpReplayingPeerLivenessProbe(IPeerLivenessProbe inner, LinkUpEventReplayer replayer,
                                            ReestablishTracker? tracker = null)
    {
        _inner = inner;
        _replayer = replayer;
        _tracker = tracker;
    }

    /// <summary>The decorated probe.</summary>
    public IPeerLivenessProbe Inner => _inner;

    /// <inheritdoc />
    public Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                   CancellationToken cancellationToken = default) =>
        _inner.IsAliveAsync(channelId, peerPubKey, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Marks the link, then schedules the replay of the channel's pending events (in the background: the
    /// caller holds the channel's lock), unless the channel was just reestablished (its caller replays them).</remarks>
    public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey)
    {
        _inner.MarkLinkUp(channelId, peerPubKey);

        if (_tracker?.GetStatus(channelId) == ReestablishStatus.Reestablished)
            return;

        _replayer.Schedule(channelId);
    }
}