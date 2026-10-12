using System.Security.Cryptography;

namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;
using Utils.Extensions;

public readonly record struct PrivKey
{
    /// <summary>
    /// The private key value.
    /// </summary>
    public byte[] Value { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PrivKey"/> struct.
    /// </summary>
    /// <param name="value">The private key value.</param>
    public PrivKey(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != CryptoConstants.PrivkeyLen)
            throw new ArgumentException($"Private key must be {CryptoConstants.PrivkeyLen} bytes long.", nameof(value));

        Value = value;
    }

    /// <summary>
    /// Compares the key bytes in constant time.
    /// </summary>
    public bool Equals(PrivKey other)
    {
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (Value is null || other.Value is null)
            return Value is null && other.Value is null;
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        return CryptographicOperations.FixedTimeEquals(Value, other.Value);
    }

    public override int GetHashCode()
    {
        return Value.GetByteArrayHashCode();
    }

    public static implicit operator PrivKey(byte[] bytes) => new(bytes);
    public static implicit operator byte[](PrivKey hash) => hash.Value;

    public static implicit operator ReadOnlySpan<byte>(PrivKey hash) => hash.Value;
}