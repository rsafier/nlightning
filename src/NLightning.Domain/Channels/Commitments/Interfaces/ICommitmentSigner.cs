namespace NLightning.Domain.Channels.Commitments.Interfaces;

using Crypto.ValueObjects;
using Splicing;
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
    /// <param name="funding">The funding the commitment spends (splicing plan SP1-0: with pending splices one
    /// commitment is signed per active funding at the same number, SP-OP-03). The engine passes
    /// <see cref="CommitmentParams.Funding"/>, the channel's current funding; null when the engine was built without
    /// funding data (tests), which means the channel's only funding. Before lane SP1-C the implementations sign against
    /// the channel's registered funding whatever this holds.</param>
    /// <param name="number">The remote commitment number being signed.</param>
    /// <param name="spec">The commitment content; its <see cref="CommitmentSpec.Holder"/> is the remote side.</param>
    /// <param name="remotePerCommitmentPoint">The peer's per-commitment point for <paramref name="number"/>.</param>
    /// <param name="remoteVerificationNonce">Simple taproot channels (NL-877 T3): the peer's verification nonce for
    /// this commitment on that funding (<see cref="ChannelCommitments.RemoteNextNonces"/>), which the engine consumes;
    /// null for the other channel types.</param>
    /// <returns>The commitment signature and one HTLC signature per untrimmed HTLC, in commitment output order; for a
    /// simple taproot channel the zero <see cref="CommitmentSignatures.Signature"/> and the
    /// <see cref="CommitmentSignatures.PartialSignature"/>.</returns>
    CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ChannelFunding? funding, ulong number,
                                              CommitmentSpec spec, CompactPubKey remotePerCommitmentPoint,
                                              MusigPublicNonce? remoteVerificationNonce = null);
}