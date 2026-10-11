namespace NLightning.Domain.Bitcoin.ValueObjects;

using Crypto.Constants;
using Utils.Extensions;

public readonly struct TxId : IEquatable<TxId>
{
    private readonly byte[] _value;

    public bool IsZero => _value is not null && _value.SequenceEqual(Zero._value);
    public bool IsOne => _value is not null && _value.SequenceEqual(One._value);

    public TxId(byte[] hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != CryptoConstants.Sha256HashLen)
            throw new ArgumentException($"TxId must be {CryptoConstants.Sha256HashLen} bytes.", nameof(hash));

        _value = hash;
    }

    public static TxId Zero => new byte[CryptoConstants.Sha256HashLen];

    public static TxId One => new byte[]
    {
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1,
    };

    public static implicit operator TxId(byte[] bytes) => new(bytes);
    public static implicit operator byte[](TxId txId) => txId._value;
    public static implicit operator ReadOnlyMemory<byte>(TxId compactPubKey) => compactPubKey._value;
    public static implicit operator ReadOnlySpan<byte>(TxId compactPubKey) => compactPubKey._value;

    public static bool operator !=(TxId left, TxId right)
    {
        return !left.Equals(right);
    }

    public static bool operator ==(TxId left, TxId right)
    {
        return left.Equals(right);
    }

    public bool Equals(TxId other)
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
        return obj is TxId other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _value.GetByteArrayHashCode();
    }

    /// <summary>
    /// The txid as bitcoind, LND, CLN and block explorers show it: the hex of the reversed (display order) bytes, so a
    /// txid copied from a log or an exception message can be looked up as it is (NL-519). The value itself stays in
    /// internal (serialized) order; <see cref="ToInternalHex"/> prints that.
    /// </summary>
    public override string ToString()
    {
        var reversed = (byte[])_value.Clone();
        Array.Reverse(reversed);
        return Convert.ToHexStringLower(reversed);
    }

    /// <summary>The hex of the internal (serialized) byte order, for deterministic sort keys.</summary>
    public string ToInternalHex() => Convert.ToHexStringLower(_value);
}