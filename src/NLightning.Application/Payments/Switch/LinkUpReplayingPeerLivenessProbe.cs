namespace NLightning.Application.Payments.Switch;

using Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Decorates the registered <see cref="IPeerLivenessProbe"/> so that marking a channel's link up also replays the
/// channel's pending HTLC events into the switch (<see cref="LinkUpEventReplayer"/>). Registered by
/// <see cref="HtlcSwitchServiceCollectionExtensions.AddHtlcSwitchServices"/>.
/// </summary>
public sealed class LinkUpReplayingPeerLivenessProbe : IPeerLivenessProbe
{
    private readonly IPeerLivenessProbe _inner;
    private readonly LinkUpEventReplayer _replayer;

    public LinkUpReplayingPeerLivenessProbe(IPeerLivenessProbe inner, LinkUpEventReplayer replayer)
    {
        _inner = inner;
        _replayer = replayer;
    }

    /// <summary>The decorated probe.</summary>
    public IPeerLivenessProbe Inner => _inner;

    /// <inheritdoc />
    public Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                   CancellationToken cancellationToken = default) =>
        _inner.IsAliveAsync(channelId, peerPubKey, cancellationToken);

    /// <inheritdoc />
    /// <remarks>Marks the link, then schedules the replay of the channel's pending events (in the background: the
    /// caller holds the channel's lock).</remarks>
    public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey)
    {
        _inner.MarkLinkUp(channelId, peerPubKey);
        _replayer.Schedule(channelId);
    }
}