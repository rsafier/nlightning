namespace NLightning.Domain.Gossip.Enums;

/// <summary>
/// The outcome of the channel proof of a <c>channel_announcement_2</c> (BOLTs PR #1059 "Channel announcement
/// validation").
/// </summary>
public enum GossipV2ProofResult
{
    /// <summary>The funding output matches the announced keys and the signature verifies.</summary>
    Valid,

    /// <summary>The funding output is neither P2WSH nor P2TR.</summary>
    UnsupportedScript,

    /// <summary>Exactly one bitcoin key, or a P2WSH announcement without both keys or with a merkle root.</summary>
    MalformedProof,

    /// <summary>The bitcoin keys (and merkle root) do not derive the funding output.</summary>
    KeyMismatch,

    /// <summary>The BIP 340 signature does not verify against the MuSig2 aggregate.</summary>
    BadSignature
}