namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// Our latest commitment: the one we can broadcast.
/// </summary>
/// <param name="Number">The commitment number (0 after the opening; +1 per received <c>commitment_signed</c>).</param>
/// <param name="Spec">Its content.</param>
/// <param name="RemoteSignatures">The peer's signatures for it (null only for a snapshot that does not carry them).</param>
public sealed record LocalCommit(ulong Number, CommitmentSpec Spec, CommitmentSignatures? RemoteSignatures);