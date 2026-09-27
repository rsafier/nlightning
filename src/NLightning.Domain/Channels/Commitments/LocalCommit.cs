namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.ValueObjects;

/// <summary>
/// Our latest commitment: the one we can broadcast.
/// </summary>
/// <param name="Number">The commitment number (0 after the opening; +1 per received <c>commitment_signed</c>).</param>
/// <param name="Spec">Its content on the current funding.</param>
/// <param name="RemoteSignatures">The peer's signatures for it on the current funding (null only for a snapshot that
/// does not carry them).</param>
public sealed record LocalCommit(ulong Number, CommitmentSpec Spec, CommitmentSignatures? RemoteSignatures)
{
    /// <summary>
    /// The peer's signatures for this same commitment number on every pending splice funding, in the order of
    /// <see cref="ChannelCommitments.PendingFundings"/> (splicing plan SP-I2: every active funding stays
    /// broadcastable). Empty for a channel without a pending splice. The content on a pending funding is
    /// <see cref="ChannelCommitments.SpecFor"/> of <see cref="Spec"/>.
    /// </summary>
    public IReadOnlyList<FundingSignatures> PendingFundingSignatures { get; init; } = [];

    /// <summary>The peer's signatures on the pending funding <paramref name="fundingTxId"/>, or null.</summary>
    public CommitmentSignatures? SignaturesFor(TxId fundingTxId) =>
        PendingFundingSignatures.FirstOrDefault(s => s.FundingTxId == fundingTxId)?.Signatures;

    public bool Equals(LocalCommit? other) =>
        other is not null
     && (ReferenceEquals(this, other)
      || (Number == other.Number && Equals(Spec, other.Spec) && Equals(RemoteSignatures, other.RemoteSignatures)
       && PendingFundingSignatures.SequenceEqual(other.PendingFundingSignatures)));

    public override int GetHashCode() =>
        HashCode.Combine(Number, Spec, RemoteSignatures, PendingFundingSignatures.Count);
}