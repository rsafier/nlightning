namespace NLightning.Domain.Channels.Interfaces;

using Commitments;
using Crypto.ValueObjects;
using Models;

/// <summary>
/// Signs the peer's next commitment for a <c>commitment_signed</c> we send.
/// </summary>
public interface ICommitmentSigner
{
    /// <summary>
    /// Builds the remote commitment <paramref name="remoteCommitmentNumber"/> from <paramref name="spec"/> (the local
    /// node's view) and returns our commitment signature and our signatures for its HTLC transactions, in commitment
    /// output order.
    /// </summary>
    /// <param name="channel">The channel (static data: keys, funding output, dust limits, anchors, funder).</param>
    /// <param name="spec">The balances, feerate and HTLCs of the commitment, from our point of view.</param>
    /// <param name="remoteCommitmentNumber">The number of the remote commitment being signed.</param>
    /// <param name="remotePerCommitmentPoint">The peer's per-commitment point for that commitment.</param>
    CommitmentTxSignatures SignRemoteCommitment(ChannelModel channel, CommitmentTxSpec spec, ulong remoteCommitmentNumber,
                                              CompactPubKey remotePerCommitmentPoint);
}