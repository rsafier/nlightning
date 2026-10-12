namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.ValueObjects;

/// <summary>
/// A peer commitment we signed and sent, whose <c>revoke_and_ack</c> we are still waiting for. While one exists we do
/// not sign again (decision D7: one outstanding <c>commitment_signed</c> per direction).
/// </summary>
/// <param name="Commit">The signed commitment (becomes <see cref="ChannelCommitments.RemoteCommit"/> on the RAA).</param>
/// <param name="SentSignatures">The signatures we sent on the current funding (kept for retransmission).</param>
public sealed record RemoteNextCommit(RemoteCommit Commit, CommitmentSignatures SentSignatures)
{
    /// <summary>
    /// The signatures we sent for the same commitment on every pending splice funding, in the order of
    /// <see cref="ChannelCommitments.PendingFundings"/> (the batch of SP-OP-03). Empty without a pending splice.
    /// </summary>
    public IReadOnlyList<FundingSignatures> PendingFundingSignatures { get; init; } = [];

    /// <summary>The signatures we sent on the pending funding <paramref name="fundingTxId"/>, or null.</summary>
    public CommitmentSignatures? SignaturesFor(TxId fundingTxId) =>
        PendingFundingSignatures.FirstOrDefault(s => s.FundingTxId == fundingTxId)?.Signatures;

    public bool Equals(RemoteNextCommit? other) =>
        other is not null
     && (ReferenceEquals(this, other)
      || (Equals(Commit, other.Commit) && Equals(SentSignatures, other.SentSignatures)
       && PendingFundingSignatures.SequenceEqual(other.PendingFundingSignatures)));

    public override int GetHashCode() => HashCode.Combine(Commit, SentSignatures, PendingFundingSignatures.Count);
}