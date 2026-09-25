namespace NLightning.Domain.Channels.Commitments.Interfaces;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// Port: builds and signs the peer's commitment transaction and its HTLC transactions (BOLT 3).
/// </summary>
/// <remarks>Implemented outside Domain (plan N3-T5 <c>CommitmentSigningService</c>); the engine only passes the
/// agreed content in.</remarks>
public interface ICommitmentSigner
{
    /// <summary>
    /// Signs the peer's commitment <paramref name="number"/> with content <paramref name="spec"/>.
    /// </summary>
    /// <returns>The commitment signature and one HTLC signature per untrimmed HTLC, in commitment output order.</returns>
    CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                              CompactPubKey remotePerCommitmentPoint);
}