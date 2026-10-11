namespace NLightning.Domain.Protocol.Interfaces;

using Crypto.ValueObjects;

/// <summary>
/// Checks the per-commitment secrets the peer reveals in <c>revoke_and_ack</c> (and <c>channel_reestablish</c>).
/// Needs no private key, so it is separate from <c>ILightningSigner</c>.
/// </summary>
public interface IPerCommitmentSecretVerifier
{
    /// <summary>
    /// Whether <paramref name="perCommitmentSecret"/> is a valid secp256k1 scalar whose point
    /// (<c>per_commitment_secret * G</c>) equals <paramref name="expectedPerCommitmentPoint"/>. BOLT 2: a receiver of
    /// <c>revoke_and_ack</c> MUST check that <c>per_commitment_secret</c> generates the previous
    /// <c>per_commitment_point</c>.
    /// </summary>
    bool Verify(Secret perCommitmentSecret, CompactPubKey expectedPerCommitmentPoint);

    /// <summary>
    /// Verifies the secret of the peer's commitment <paramref name="remoteCommitmentNumber"/> against its point and,
    /// only if it matches, inserts it into the peer's shachain at index <c>2^48 - 1 - remoteCommitmentNumber</c>, which
    /// also checks it against every secret received before (BOLT 3 <c>insert_secret</c>).
    /// </summary>
    /// <returns>
    /// False when the secret does not generate the point (the shachain is not touched) or the shachain rejects it
    /// (not generated from the same seed, or out of order).
    /// </returns>
    bool VerifyAndStore(Secret perCommitmentSecret, ulong remoteCommitmentNumber,
                        CompactPubKey expectedPerCommitmentPoint, ISecretStorageService remoteShachain);
}