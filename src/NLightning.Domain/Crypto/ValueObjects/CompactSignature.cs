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