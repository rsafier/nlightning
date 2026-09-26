namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// A peer commitment we signed and sent, whose <c>revoke_and_ack</c> we are still waiting for. While one exists we do
/// not sign again (decision D7: one outstanding <c>commitment_signed</c> per direction).
/// </summary>
/// <param name="Commit">The signed commitment (becomes <see cref="ChannelCommitments.RemoteCommit"/> on the RAA).</param>
/// <param name="SentSignatures">The signatures we sent (kept for retransmission).</param>
public sealed record RemoteNextCommit(RemoteCommit Commit, CommitmentSignatures SentSignatures);