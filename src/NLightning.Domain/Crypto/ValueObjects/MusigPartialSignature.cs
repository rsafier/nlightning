namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;
using Utils.Extensions;

/// <summary>
/// A BIP 327 (MuSig2) partial signature: the 32-byte big-endian <c>s</c> value.
/// </summary>
/// <remarks>
/// Only the length is checked here; a value at or above the curve order fails
/// <see cref="Interfaces.IMusig2Service.VerifyPartialSignature"/> and is an invalid contribution to
/// <see cref="Interfaces.IMusig2Service.AggregatePartialSignatures"/>. Converts implicitly from
/// <see cref="ReadOnlySpan{T}"/> (a <c>byte[]</c> converts through it, as a copy), so <c>cond ? null : sig</c> is
/// <c>MusigPartialSignature?</c> instead of a runtime exception.
/// </remarks>
public readonly struct MusigPartialSignature : IEquatable<MusigPartialSignature>
{
    private readonly byte[] _value;

    public MusigPartialSignature(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != MusigConstants.PartialSignatureLen)
            throw new ArgumentException(
                $"A MuSig2 partial signature must be {MusigConstants.PartialSignatureLen} bytes.", nameof(value));

        _value = value;
    }

    public static implicit operator MusigPartialSignature(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());
    public static implicit operator byte[](MusigPartialSignature signature) => signature._value;
    public static implicit operator ReadOnlySpan<byte>(MusigPartialSignature signature) => signature._value;
    public static implicit operator ReadOnlyMemory<byte>(MusigPartialSignature signature) => signature._value;

    public static bool operator ==(MusigPartialSignature left, MusigPartialSignature right) => left.Equals(right);
    public static bool operator !=(MusigPartialSignature left, MusigPartialSignature right) => !left.Equals(right);

    public override string ToString() => _value is null ? string.Empty : Convert.ToHexStringLower(_value);

    public bool Equals(MusigPartialSignature other)
    {
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (_value is null || other._value is null)
            return _value is null && other._value is null;
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        return _value.AsSpan().SequenceEqual(other._value);
    }

    public override bool Equals(object? obj) => obj is MusigPartialSignature other && Equals(other);

    public override int GetHashCode() => _value.GetByteArrayHashCode();
}