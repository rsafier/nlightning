namespace NLightning.Domain.Channels.Interfaces;

using Commitments;
using Crypto.ValueObjects;
using Models;

/// <summary>
/// Verifies the signatures of a received <c>commitment_signed</c> against our next local commitment.
/// </summary>
public interface ICommitmentVerifier
{
    /// <summary>
    /// Builds our local commitment <paramref name="localCommitmentNumber"/> from <paramref name="spec"/> and checks the
    /// peer's commitment signature and its HTLC signatures (count, order, low-S, validity).
    /// </summary>
    /// <returns>The verified signatures with the commitment txid, ready to persist.</returns>
    /// <exception cref="Exceptions.SignerException">A signature is missing, malformed, high-S or invalid.</exception>
    CommitmentTxSignatures VerifyLocalCommitment(ChannelModel channel, CommitmentTxSpec spec, ulong localCommitmentNumber,
                                               CompactSignature signature,
                                               IReadOnlyList<CompactSignature> htlcSignatures);
}