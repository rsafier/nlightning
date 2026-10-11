namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;
using Domain.Interfaces;
using Utils.Extensions;

public record CompactSignature : IValueObject
{
    public byte[] Value { get; }

    public CompactSignature(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is < CryptoConstants.MinSignatureSize or > CryptoConstants.MaxSignatureSize)
            throw new ArgumentOutOfRangeException(nameof(value),
                                                  $"Signature must be less than or equal to {CryptoConstants.MaxSignatureSize} bytes");

        Value = value;
    }

    /// <summary>
    /// A new all-zero 64-byte signature: the <c>signature</c> field of <c>funding_created</c>, <c>funding_signed</c> and
    /// <c>commitment_signed</c> in a simple taproot channel, whose real signature is the MuSig2 partial signature in the
    /// <c>partial_signature_with_nonce</c> TLV.
    /// </summary>
    public static CompactSignature Zero => new(new byte[CryptoConstants.MaxSignatureSize]);

    /// <summary>
    /// Whether this is the all-zero 64-byte placeholder of a simple taproot channel (<see cref="Zero"/>).
    /// </summary>
    public bool IsZero => Value.Length == CryptoConstants.MaxSignatureSize && !Value.AsSpan().ContainsAnyExcept((byte)0);

    public virtual bool Equals(CompactSignature? other)
    {
        if (other is null)
            return false;

        return ReferenceEquals(this, other) || Value.AsSpan().SequenceEqual(other.Value);
    }

    public override int GetHashCode()
    {
        return Value.GetByteArrayHashCode();
    }

    public static implicit operator CompactSignature(byte[] bytes) => new(bytes);
    public static implicit operator byte[](CompactSignature hash) => hash.Value;

    public static implicit operator ReadOnlyMemory<byte>(CompactSignature compactPubKey) => compactPubKey.Value;
}