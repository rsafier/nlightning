namespace NLightning.Domain.Channels.Commitments.Interfaces;

using Crypto.ValueObjects;

/// <summary>
/// Port: checks a <c>per_commitment_secret</c> received in <c>revoke_and_ack</c> (B2-RAA-R01).
/// </summary>
/// <remarks>Needs no private key (<c>secret * G == point</c>); implemented in Infrastructure.Bitcoin (plan N3-T3). The
/// shachain insert (B2-RAA-R02/R04) is done by the caller after the engine accepts the RAA.</remarks>
public interface IRevocationVerifier
{
    /// <summary>True when <paramref name="perCommitmentSecret"/> is a valid private key whose public key is
    /// <paramref name="expectedPerCommitmentPoint"/>.</summary>
    bool IsValidSecret(Secret perCommitmentSecret, CompactPubKey expectedPerCommitmentPoint);
}