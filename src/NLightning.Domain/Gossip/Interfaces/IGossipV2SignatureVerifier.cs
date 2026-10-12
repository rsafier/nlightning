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

    /// <summary>
    /// The MuSig2 signature of a <c>channel_announcement_2</c> with both bitcoin keys, checked without the funding
    /// output (NL-1140): the aggregate <c>KeyAgg(KeySort(node_id_1, node_id_2, bitcoin_key_1, bitcoin_key_2))</c> does
    /// not depend on it, so the ingress drops a forged announcement before it costs a chain lookup.
    /// <see cref="CheckChannelProof"/> still matches the output against the keys afterwards.
    /// </summary>
    /// <returns>
    /// <see cref="GossipV2ProofResult.Valid"/>, <see cref="GossipV2ProofResult.BadSignature"/>,
    /// <see cref="GossipV2ProofResult.KeyMismatch"/> when a key is not on the curve, or
    /// <see cref="GossipV2ProofResult.MalformedProof"/> when the announcement lacks a bitcoin key (the 3-key form needs
    /// the output key).
    /// </returns>
    GossipV2ProofResult CheckChannelSignature(ChannelAnnouncement2Payload announcement);
}