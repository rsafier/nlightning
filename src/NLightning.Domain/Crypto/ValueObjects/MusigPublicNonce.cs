namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;
using Utils.Extensions;

/// <summary>
/// A BIP 327 (MuSig2) public nonce: two 33-byte compressed points, <c>R1 || R2</c> (66 bytes).
/// </summary>
/// <remarks>
/// Only the length is checked here; whether both halves are points on the curve is checked by
/// <see cref="Interfaces.IMusig2Service"/>, which reports a bad one as an invalid contribution. Converts implicitly from
/// <see cref="ReadOnlySpan{T}"/> (a <c>byte[]</c> converts through it, as a copy), so <c>cond ? null : nonce</c> is
/// <c>MusigPublicNonce?</c> instead of a runtime exception.
/// </remarks>
public readonly struct MusigPublicNonce : IEquatable<MusigPublicNonce>
{
    private readonly byte[] _value;

    public MusigPublicNonce(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != MusigConstants.PublicNonceLen)
            throw new ArgumentException($"A MuSig2 public nonce must be {MusigConstants.PublicNonceLen} bytes.",
                                        nameof(value));

        _value = value;
    }

    public static implicit operator MusigPublicNonce(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());
    public static implicit operator byte[](MusigPublicNonce nonce) => nonce._value;
    public static implicit operator ReadOnlySpan<byte>(MusigPublicNonce nonce) => nonce._value;
    public static implicit operator ReadOnlyMemory<byte>(MusigPublicNonce nonce) => nonce._value;

    public static bool operator ==(MusigPublicNonce left, MusigPublicNonce right) => left.Equals(right);
    public static bool operator !=(MusigPublicNonce left, MusigPublicNonce right) => !left.Equals(right);

    public override string ToString() => _value is null ? string.Empty : Convert.ToHexStringLower(_value);

    public bool Equals(MusigPublicNonce other)
    {
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (_value is null || other._value is null)
            return _value is null && other._value is null;
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        return _value.AsSpan().SequenceEqual(other._value);
    }

    public override bool Equals(object? obj) => obj is MusigPublicNonce other && Equals(other);

    public override int GetHashCode() => _value.GetByteArrayHashCode();
}