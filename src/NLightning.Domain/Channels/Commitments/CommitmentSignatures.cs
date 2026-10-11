namespace NLightning.Domain.Channels.Commitments;

using Crypto.ValueObjects;

/// <summary>
/// The signatures of a <c>commitment_signed</c>: the commitment signature and one HTLC signature per untrimmed HTLC, in
/// BOLT 3 commitment output order.
/// </summary>
/// <remarks>The engine treats them as opaque; producing and checking them is done behind
/// <see cref="Interfaces.ICommitmentSigner"/> and <see cref="Interfaces.ICommitmentVerifier"/>.</remarks>
public sealed record CommitmentSignatures(CompactSignature Signature, IReadOnlyList<CompactSignature> HtlcSignatures)
{
    /// <summary>The all-zero 64-byte <c>signature</c> field a simple taproot <c>commitment_signed</c> carries.</summary>
    public static CompactSignature ZeroSignature => new(new byte[64]);

    /// <summary>
    /// Simple taproot channels (bolt-simple-taproot.md, NL-877 T3): the MuSig2 partial signature of the commitment with
    /// its signer's nonce (<c>partial_signature_with_nonce</c>): the peer's for our commitment, ours for the peer's.
    /// <see cref="Signature"/> is then <see cref="ZeroSignature"/>. Null for the other channel types.
    /// </summary>
    public MusigPartialSignatureWithNonce? PartialSignature { get; init; }

    /// <summary>The signatures of a simple taproot commitment: a zero <see cref="Signature"/> and the partial one.</summary>
    public static CommitmentSignatures Taproot(MusigPartialSignatureWithNonce partialSignature,
                                               IReadOnlyList<CompactSignature> htlcSignatures) =>
        new(ZeroSignature, htlcSignatures) { PartialSignature = partialSignature };

    public bool Equals(CommitmentSignatures? other) =>
        other is not null
     && (ReferenceEquals(this, other)
      || (Signature.Equals(other.Signature) && HtlcSignatures.SequenceEqual(other.HtlcSignatures)
       && Nullable.Equals(PartialSignature, other.PartialSignature)));

    public override int GetHashCode() => HashCode.Combine(Signature, HtlcSignatures.Count, PartialSignature);
}