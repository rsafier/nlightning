namespace NLightning.Application.Channels.Reestablish;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Interfaces;

/// <summary>
/// An <see cref="IPeerLivenessProbe"/> that also requires the channel to be reestablished (or opened) on the peer's
/// current connection (BOLT 2: nothing but <c>channel_reestablish</c> goes out for a channel until the peer's arrived,
/// B2-RE-07). Every send-side update and every <c>commitment_signed</c> of the scheduler asks the probe, so they are
/// all gated.
/// </summary>
/// <remarks>
/// The inner probe pins the connection a channel was marked up on. The tracker is reset synchronously when the peer's
/// connection changes, so even a link pinned to a new connection by a reestablish that finished on the old one stays
/// down until the new connection's reestablish.
/// </remarks>
public sealed class ReestablishGatedLivenessProbe : IPeerLivenessProbe
{
    private readonly IPeerLivenessProbe _inner;
    private readonly ReestablishTracker _tracker;

    public ReestablishGatedLivenessProbe(IPeerLivenessProbe inner, ReestablishTracker tracker)
    {
        _inner = inner;
        _tracker = tracker;
    }

    /// <inheritdoc />
    public async Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                         CancellationToken cancellationToken = default) =>
        _tracker.IsReestablished(channelId) && await _inner.IsAliveAsync(channelId, peerPubKey, cancellationToken);

    /// <inheritdoc />
    public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey) => _inner.MarkLinkUp(channelId, peerPubKey);
}