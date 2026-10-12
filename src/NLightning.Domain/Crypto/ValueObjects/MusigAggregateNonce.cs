namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;
using Utils.Extensions;

/// <summary>
/// A BIP 327 (MuSig2) aggregate nonce, the output of NonceAgg: two 33-byte points <c>R1 || R2</c> (66 bytes), where
/// either half may be the point at infinity, encoded as 33 zero bytes.
/// </summary>
/// <remarks>
/// Only the length is checked here; <see cref="Interfaces.IMusig2Service"/> reports an undecodable half as an invalid
/// aggregate nonce. Converts implicitly from <see cref="ReadOnlySpan{T}"/> (a <c>byte[]</c> converts through it, as a
/// copy), so <c>cond ? null : nonce</c> is <c>MusigAggregateNonce?</c> instead of a runtime exception.
/// </remarks>
public readonly struct MusigAggregateNonce : IEquatable<MusigAggregateNonce>
{
    private readonly byte[] _value;

    public MusigAggregateNonce(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != MusigConstants.AggregateNonceLen)
            throw new ArgumentException($"A MuSig2 aggregate nonce must be {MusigConstants.AggregateNonceLen} bytes.",
                                        nameof(value));

        _value = value;
    }

    public static implicit operator MusigAggregateNonce(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());
    public static implicit operator byte[](MusigAggregateNonce nonce) => nonce._value;
    public static implicit operator ReadOnlySpan<byte>(MusigAggregateNonce nonce) => nonce._value;
    public static implicit operator ReadOnlyMemory<byte>(MusigAggregateNonce nonce) => nonce._value;

    public static bool operator ==(MusigAggregateNonce left, MusigAggregateNonce right) => left.Equals(right);
    public static bool operator !=(MusigAggregateNonce left, MusigAggregateNonce right) => !left.Equals(right);

    public override string ToString() => _value is null ? string.Empty : Convert.ToHexStringLower(_value);

    public bool Equals(MusigAggregateNonce other)
    {
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (_value is null || other._value is null)
            return _value is null && other._value is null;
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        return _value.AsSpan().SequenceEqual(other._value);
    }

    public override bool Equals(object? obj) => obj is MusigAggregateNonce other && Equals(other);

    public override int GetHashCode() => _value.GetByteArrayHashCode();
}