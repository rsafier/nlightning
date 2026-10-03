namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;
using Utils.Extensions;

public readonly struct CompactPubKey : IEquatable<CompactPubKey>
{
    private readonly byte[] _value;

    public CompactPubKey(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != CryptoConstants.CompactPubkeyLen)
            throw new ArgumentException($"PublicKey must be {CryptoConstants.CompactPubkeyLen} bytes.", nameof(value));

        if (value[0] != 0x02 && value[0] != 0x03)
            throw new ArgumentException("Invalid CompactPubKey format. The first byte must be 0x02 or 0x03.",
                                        nameof(value));

        _value = value;
    }

    /// <summary>
    /// Converts bytes (a <c>byte[]</c> converts through the implicit span conversion) into a copy-backed key.
    /// </summary>
    /// <remarks>
    /// The source is a span, not <c>byte[]</c>, so a bare <c>null</c> literal cannot bind to this operator:
    /// <c>cond ? null : key</c> is typed <c>CompactPubKey?</c> instead of throwing at runtime.
    /// </remarks>
    public static implicit operator CompactPubKey(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());
    public static implicit operator byte[](CompactPubKey hash) => hash._value;

    public static implicit operator ReadOnlySpan<byte>(CompactPubKey compactPubKey) => compactPubKey._value;

    public static implicit operator ReadOnlyMemory<byte>(CompactPubKey compactPubKey) => compactPubKey._value;

    public static bool operator !=(CompactPubKey left, CompactPubKey right)
    {
        return !left.Equals(right);
    }

    public static bool operator ==(CompactPubKey left, CompactPubKey right)
    {
        return left.Equals(right);
    }

    public override string ToString() => Convert.ToHexStringLower(_value);

    public bool Equals(CompactPubKey other)
    {
        // Handle null cases first
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (_value is null && other._value is null)
            return true;

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (_value is null || other._value is null)
            return false;

        return _value.SequenceEqual(other._value);
    }

    public override bool Equals(object? obj)
    {
        if (obj is null)
            return false;

        return obj is CompactPubKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _value.GetByteArrayHashCode();
    }
}