namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// Whose commitment is on chain, for mapping its outputs to descriptors (BOLT 5 plan §3.3).
/// </summary>
public enum CommitmentCase : byte
{
    /// <summary>Our commitment (we are its holder): <c>to_local</c> is ours after the CSV delay.</summary>
    Local = 1,

    /// <summary>
    /// An unrevoked commitment of the peer (current, or the one awaiting its <c>revoke_and_ack</c>): <c>to_remote</c>
    /// is ours, HTLC outputs are claimed directly.
    /// </summary>
    Remote = 2,

    /// <summary>A revoked commitment of the peer: every output is penalized with the revocation key.</summary>
    Revoked = 3
}