namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;

/// <summary>
/// The simple taproot channels <c>PartialSignatureWithNonce</c>: a partial signature and the public nonce its signer
/// used, <c>s || R1 || R2</c> (32 + 66 = 98 bytes), the payload of the <c>partial_signature_with_nonce</c> TLV.
/// </summary>
/// <remarks>
/// Converts implicitly from <see cref="ReadOnlySpan{T}"/> (a <c>byte[]</c> converts through it, as a copy), so
/// <c>cond ? null : value</c> is <c>MusigPartialSignatureWithNonce?</c> instead of a runtime exception.
/// </remarks>
public readonly struct MusigPartialSignatureWithNonce : IEquatable<MusigPartialSignatureWithNonce>
{
    public MusigPartialSignature PartialSignature { get; }
    public MusigPublicNonce PublicNonce { get; }

    public MusigPartialSignatureWithNonce(MusigPartialSignature partialSignature, MusigPublicNonce publicNonce)
    {
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if ((byte[])partialSignature is null)
            throw new ArgumentException("The partial signature is empty.", nameof(partialSignature));
        if ((byte[])publicNonce is null)
            throw new ArgumentException("The public nonce is empty.", nameof(publicNonce));
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        PartialSignature = partialSignature;
        PublicNonce = publicNonce;
    }

    /// <summary>
    /// Reads <c>s || R1 || R2</c>; throws <see cref="ArgumentException"/> unless exactly 98 bytes.
    /// </summary>
    public MusigPartialSignatureWithNonce(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != MusigConstants.PartialSignatureWithNonceLen)
            throw new ArgumentException(
                $"A partial signature with nonce must be {MusigConstants.PartialSignatureWithNonceLen} bytes.",
                nameof(bytes));

        PartialSignature = bytes[..MusigConstants.PartialSignatureLen];
        PublicNonce = bytes[MusigConstants.PartialSignatureLen..];
    }

    /// <summary>
    /// <c>s || R1 || R2</c>, a new array.
    /// </summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[MusigConstants.PartialSignatureWithNonceLen];
        ((ReadOnlySpan<byte>)PartialSignature).CopyTo(bytes);
        ((ReadOnlySpan<byte>)PublicNonce).CopyTo(bytes.AsSpan(MusigConstants.PartialSignatureLen));
        return bytes;
    }

    public static implicit operator MusigPartialSignatureWithNonce(ReadOnlySpan<byte> bytes) => new(bytes);
    public static implicit operator byte[](MusigPartialSignatureWithNonce value) => value.ToBytes();

    public static bool operator ==(MusigPartialSignatureWithNonce left, MusigPartialSignatureWithNonce right) =>
        left.Equals(right);

    public static bool operator !=(MusigPartialSignatureWithNonce left, MusigPartialSignatureWithNonce right) =>
        !left.Equals(right);

    public override string ToString() => $"{PartialSignature}{PublicNonce}";

    public bool Equals(MusigPartialSignatureWithNonce other) =>
        PartialSignature.Equals(other.PartialSignature) && PublicNonce.Equals(other.PublicNonce);

    public override bool Equals(object? obj) => obj is MusigPartialSignatureWithNonce other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(PartialSignature, PublicNonce);
}