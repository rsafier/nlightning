namespace NLightning.Domain.Channels.Commitments.Interfaces;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// Port: builds and signs the peer's commitment transaction and its HTLC transactions (BOLT 3).
/// </summary>
/// <remarks>
/// The only commitment signer port (NL-230). Implemented in Application by <c>EngineCommitmentSignerPort</c>, which
/// adapts the spec with <see cref="CommitmentTxSpec.FromCommitmentSpec"/> and signs through
/// <c>CommitmentSigningService</c>; the engine only passes the agreed content in.
/// </remarks>
public interface ICommitmentSigner
{
    /// <summary>
    /// Signs the peer's commitment <paramref name="number"/> with content <paramref name="spec"/>.
    /// </summary>
    /// <param name="channelId">The channel (the implementation supplies its static data).</param>
    /// <param name="number">The remote commitment number being signed.</param>
    /// <param name="spec">The commitment content; its <see cref="CommitmentSpec.Holder"/> is the remote side.</param>
    /// <param name="remotePerCommitmentPoint">The peer's per-commitment point for <paramref name="number"/>.</param>
    /// <returns>The commitment signature and one HTLC signature per untrimmed HTLC, in commitment output order.</returns>
    CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                              CompactPubKey remotePerCommitmentPoint);
}