namespace NLightning.Domain.Channels.Commitments;

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

    public bool Equals(RemoteCommit? other) =>
        other is not null
     && (ReferenceEquals(this, other)
      || (Number == other.Number && Equals(Spec, other.Spec) && Equals(PerCommitmentPoint, other.PerCommitmentPoint)
       && (SignedOnFundings is null
               ? other.SignedOnFundings is null
               : other.SignedOnFundings is not null && SignedOnFundings.SequenceEqual(other.SignedOnFundings))));

    public override int GetHashCode() => HashCode.Combine(Number, Spec, PerCommitmentPoint);
}