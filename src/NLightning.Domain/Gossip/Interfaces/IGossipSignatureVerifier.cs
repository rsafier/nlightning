namespace NLightning.Domain.Gossip.Interfaces;

using Crypto.ValueObjects;
using Models;

/// <summary>
/// Verifies BOLT 7 gossip signatures (channel_announcement, node_announcement, channel_update and
/// announcement_signatures). The caller hashes: every signature is over the double-SHA256 of the message's signed
/// range (for example <c>ChannelUpdatePayload.GetSignatureHash()</c>).
/// </summary>
/// <remarks>
/// Implementations accept high-S signatures (BOLT 7: a relaying node may replace s with -s) and return false, never
/// throw, for an unparsable signature, a public key that is not a curve point, or a default value.
/// </remarks>
public interface IGossipSignatureVerifier
{
    /// <summary>
    /// True when <paramref name="signature"/> is a valid signature of <paramref name="messageHash"/> by
    /// <paramref name="publicKey"/>.
    /// </summary>
    bool Verify(Hash messageHash, CompactSignature signature, CompactPubKey publicKey);

    /// <summary>
    /// True when every check verifies (a channel_announcement has 4, an announcement_signatures 2). An empty list is
    /// false, so a caller that built no checks never accepts a message.
    /// </summary>
    bool VerifyAll(IReadOnlyList<GossipSignatureCheck> checks);
}