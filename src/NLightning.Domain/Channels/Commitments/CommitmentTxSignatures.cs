namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// The signatures of one commitment, as carried by <c>commitment_signed</c>: the commitment signature and one HTLC
/// signature per HTLC output, in commitment output order.
/// </summary>
/// <param name="CommitmentTxId">The txid of the (unsigned) commitment transaction the signatures commit to.</param>
/// <param name="Signature">The funding-key signature of the commitment transaction.</param>
/// <param name="HtlcSignatures">The HTLC transaction signatures, in commitment output order.</param>
public sealed record CommitmentTxSignatures(
    TxId CommitmentTxId,
    CompactSignature Signature,
    IReadOnlyList<CompactSignature> HtlcSignatures);