namespace NLightning.Domain.Channels.Commitments;

using Crypto.ValueObjects;

/// <summary>
/// A commitment of the peer that we signed.
/// </summary>
/// <param name="Number">The commitment number (0 after the opening; +1 per sent <c>commitment_signed</c>).</param>
/// <param name="Spec">Its content.</param>
/// <param name="PerCommitmentPoint">The peer's per-commitment point for <paramref name="Number"/>; its secret must be
/// revealed in the <c>revoke_and_ack</c> that revokes this commitment.</param>
public sealed record RemoteCommit(ulong Number, CommitmentSpec Spec, CompactPubKey PerCommitmentPoint);