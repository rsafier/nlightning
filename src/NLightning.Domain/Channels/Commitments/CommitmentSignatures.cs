namespace NLightning.Domain.Channels.Commitments;

using Crypto.ValueObjects;

/// <summary>
/// The signatures of a <c>commitment_signed</c>: the commitment signature and one HTLC signature per untrimmed HTLC, in
/// BOLT 3 commitment output order.
/// </summary>
/// <remarks>The engine treats them as opaque; producing and checking them is done behind
/// <see cref="Interfaces.ICommitmentSigner"/> and <see cref="Interfaces.ICommitmentVerifier"/>.</remarks>
public sealed record CommitmentSignatures(CompactSignature Signature, IReadOnlyList<CompactSignature> HtlcSignatures);