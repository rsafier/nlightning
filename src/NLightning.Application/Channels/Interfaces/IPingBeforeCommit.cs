namespace NLightning.Application.Channels.Interfaces;

using Domain.Crypto.ValueObjects;

/// <summary>
/// BOLT 2 <c>commitment_signed</c> sender: "if it has not recently received a message from the remote node: SHOULD use
/// <c>ping</c> and await the reply <c>pong</c> before sending <c>commitment_signed</c>" (B2-CS-S05, NL-251).
/// </summary>
public interface IPingBeforeCommit
{
    /// <summary>
    /// True when a message arrived from <paramref name="peerPubKey"/> recently, or when it answered a <c>ping</c> sent
    /// now in time. False when the peer is not connected or did not answer (the connection is then closed, BOLT 1 MAY;
    /// the channel_reestablish of the next connection signs what was held back).
    /// </summary>
    Task<bool> EnsureResponsiveAsync(CompactPubKey peerPubKey, CancellationToken cancellationToken = default);
}