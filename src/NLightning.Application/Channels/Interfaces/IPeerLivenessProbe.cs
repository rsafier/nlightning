namespace NLightning.Application.Channels.Interfaces;

using Domain.Crypto.ValueObjects;

/// <summary>
/// The commit scheduler's "ping before commit" check (BOLT2 plan N6-T2): whether a peer is worth sending a
/// <c>commitment_signed</c> to right now.
/// </summary>
/// <remarks>
/// A commitment signed for a dead connection is not lost (it is persisted with its diff and re-sent verbatim on
/// channel_reestablish), but signing only for a live peer avoids piling state onto a connection that is going away.
/// The default implementation (<c>ConnectedPeerLivenessProbe</c>) checks that the peer has a connection; a probe that
/// sends a BOLT 1 <c>ping</c> when nothing was received recently needs something like
/// <c>IPeerService.LastMessageReceivedAt</c>, which does not exist yet.
/// </remarks>
public interface IPeerLivenessProbe
{
    /// <summary>True when <paramref name="peerPubKey"/> is connected and responsive.</summary>
    Task<bool> IsAliveAsync(CompactPubKey peerPubKey, CancellationToken cancellationToken = default);
}