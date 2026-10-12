namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Splicing;

/// <summary>
/// A commitment of the peer that we signed.
/// </summary>
/// <param name="Number">The commitment number (0 after the opening; +1 per sent <c>commitment_signed</c>).</param>
/// <param name="Spec">Its content.</param>
/// <param name="PerCommitmentPoint">The peer's per-commitment point for <paramref name="Number"/>; its secret must be
/// revealed in the <c>revoke_and_ack</c> that revokes this commitment.</param>
public sealed record RemoteCommit(ulong Number, CommitmentSpec Spec, CompactPubKey PerCommitmentPoint)
{
    /// <summary>
    /// Every funding this commitment was signed on, the then-current one first, when splices were pending at signing
    /// (SP-OP-03); null otherwise (the current funding only). Kept until the commitment is revoked, so the revocation
    /// log also covers fundings discarded or replaced by a lock in between (SP-I5).
    /// </summary>
    public IReadOnlyList<ChannelFunding>? SignedOnFundings { get; init; }

    /// <summary>
    /// What the snapshot stores of <see cref="SignedOnFundings"/> (NL-494): the funding txids in order, null when it is
    /// null. The funding records themselves are the channel's <c>ChannelFundings</c> rows, discarded and replaced ones
    /// included, so <see cref="WithSignedOnFundings"/> rebuilds the list from the txids at load time.
    /// </summary>
    public IReadOnlyList<TxId>? GetSignedOnFundingTxIds() =>
        SignedOnFundings?.Select(f => f.FundingTxId).ToList();

    /// <summary>
    /// The commitment with <see cref="SignedOnFundings"/> restored from the stored txids (NL-494;
    /// <c>IChannelStateDbRepository.LoadAsync</c>): each txid resolved against <paramref name="fundings"/> (every
    /// funding row of the channel, whatever its status), in the stored order; null txids leave it null.
    /// </summary>
    /// <exception cref="InvalidOperationException">A stored txid names no funding of the channel.</exception>
    public RemoteCommit WithSignedOnFundings(IReadOnlyList<TxId>? fundingTxIds,
                                             IReadOnlyCollection<ChannelFunding> fundings)
    {
        ArgumentNullException.ThrowIfNull(fundings);
        if (fundingTxIds is null)
            return this with { SignedOnFundings = null };

        var restored = new List<ChannelFunding>(fundingTxIds.Count);
        foreach (var fundingTxId in fundingTxIds)
            restored.Add(fundings.FirstOrDefault(f => f.FundingTxId == fundingTxId)
                      ?? throw new InvalidOperationException(
                             $"Remote commitment {Number} was signed on funding {fundingTxId}, which is not a funding of the channel"));

        return this with { SignedOnFundings = restored };
    }

    public bool Equals(RemoteCommit? other) =>
        other is not null
     && (ReferenceEquals(this, other)
      || (Number == other.Number && Equals(Spec, other.Spec) && Equals(PerCommitmentPoint, other.PerCommitmentPoint)
       && (SignedOnFundings is null
               ? other.SignedOnFundings is null
               : other.SignedOnFundings is not null && SignedOnFundings.SequenceEqual(other.SignedOnFundings))));

    public override int GetHashCode() => HashCode.Combine(Number, Spec, PerCommitmentPoint);
}