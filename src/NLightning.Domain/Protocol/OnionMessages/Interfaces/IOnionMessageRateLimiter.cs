namespace NLightning.Domain.Protocol.OnionMessages.Interfaces;

using Crypto.ValueObjects;

/// <summary>
/// Admits or drops incoming onion messages (BOLT 4 reader MAY rate-limit by dropping; nothing is sent back): a token
/// bucket per peer and a global one, in bytes and in messages.
/// </summary>
/// <remarks>Thread-safe.</remarks>
public interface IOnionMessageRateLimiter
{
    /// <summary>
    /// Takes the tokens for one message from <paramref name="peerNodeId"/>.
    /// </summary>
    /// <param name="peerNodeId">The peer it arrived from.</param>
    /// <param name="messageLength">Its length in bytes (the <c>onion_message_packet</c>).</param>
    /// <returns>True to process the message; false to drop it.</returns>
    bool TryAdmit(CompactPubKey peerNodeId, int messageLength);

    /// <summary>
    /// Forgets a disconnected peer's bucket.
    /// </summary>
    void RemovePeer(CompactPubKey peerNodeId);
}