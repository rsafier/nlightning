using System.Security.Cryptography;

namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;
using Utils.Extensions;

public readonly struct Secret : IEquatable<Secret>
{
    private readonly byte[] _value;
    public static Secret Empty => new(new byte[CryptoConstants.SecretLen]);

    public Secret(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != CryptoConstants.SecretLen)
            throw new ArgumentOutOfRangeException(nameof(value), value.Length,
                                                  $"Secret must have {CryptoConstants.SecretLen} bytes.");

        _value = value;
    }

    public static implicit operator Secret(byte[] bytes) => new(bytes);
    public static implicit operator byte[](Secret hash) => hash._value;

    public static implicit operator ReadOnlyMemory<byte>(Secret hash) => hash._value;
    public static implicit operator ReadOnlySpan<byte>(Secret hash) => hash._value;

    /// <summary>
    /// Compares the secrets in constant time.
    /// </summary>
    public bool Equals(Secret other)
    {
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (_value is null || other._value is null)
            return _value is null && other._value is null;
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        return CryptographicOperations.FixedTimeEquals(_value, other._value);
    }

    public override bool Equals(object? obj)
    {
        return obj is Secret other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _value.GetByteArrayHashCode();
    }

    public static bool operator ==(Secret left, Secret right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Secret left, Secret right)
    {
        return !left.Equals(right);
    }
}