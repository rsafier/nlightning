namespace NLightning.Domain.Gossip.Interfaces;

using Crypto.ValueObjects;
using Enums;
using Protocol.Payloads;

/// <summary>
/// The signature checks of taproot gossip (BOLTs PR #1059, NL-878): BIP 340 signatures of <c>channel_update_2</c> and
/// <c>node_announcement_2</c> by a node key, and the channel proof of a <c>channel_announcement_2</c> (the funding
/// output against the announced keys, then the MuSig2 aggregate's BIP 340 signature).
/// </summary>
public interface IGossipV2SignatureVerifier
{
    /// <summary>
    /// Whether <paramref name="signature"/> is a valid BIP 340 signature of <paramref name="messageHash"/> by the
    /// x-only form of <paramref name="publicKey"/>.
    /// </summary>
    bool VerifyBip340(Hash messageHash, CompactSignature signature, CompactPubKey publicKey);

    /// <summary>
    /// The draft's "Channel announcement validation" of <paramref name="announcement"/> against the funding output's
    /// <paramref name="fundingScriptPubKey"/> (P2WSH or P2TR).
    /// </summary>
    GossipV2ProofResult CheckChannelProof(ChannelAnnouncement2Payload announcement,
                                          ReadOnlySpan<byte> fundingScriptPubKey);
}