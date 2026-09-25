namespace NLightning.Domain.Channels.Commitments.Interfaces;

using ValueObjects;

/// <summary>
/// Port: checks the peer's signatures for our new commitment (BOLT 2 <c>commitment_signed</c> receiver rules
/// B2-CS-R01 and B2-CS-R03).
/// </summary>
public interface ICommitmentVerifier
{
    /// <summary>
    /// Builds our commitment <paramref name="number"/> from <paramref name="spec"/> and checks the commitment signature
    /// and every HTLC signature (valid and low-S, in output order).
    /// </summary>
    /// <returns>False when any signature is invalid; the engine then rejects the <c>commitment_signed</c>.</returns>
    bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec, CommitmentSignatures signatures);
}