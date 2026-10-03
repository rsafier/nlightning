using System.Security.Cryptography;

namespace NLightning.Domain.Crypto.ValueObjects;

using Constants;

/// <summary>
/// A BIP 327 (MuSig2) secret nonce, <c>k1 || k2 || pk</c> (32 + 32 + 33 = 97 bytes), usable for exactly one signature.
/// </summary>
/// <remarks>
/// <para>
/// A class, not a value struct, so a copy is the same nonce: signing consumes it (the scalars are zeroed, as BIP 327's
/// Sign overwrites its secnonce argument) and any later use, through any reference, is refused. Signing twice with one
/// secret nonce on different messages reveals the private key.
/// </para>
/// <para>
/// There is no conversion to bytes and <see cref="ToString"/> never shows the secret. The bytes go out only through
/// the internal <see cref="Consume"/>, which <see cref="Interfaces.IMusig2Service.Sign"/>'s implementation calls.
/// <see cref="Dispose"/> (or <see cref="Clear"/>) zeroes everything; dispose a nonce that will never be used.
/// </para>
/// </remarks>
public sealed class MusigSecretNonce : IDisposable
{
    private const int StateLive = 0;
    private const int StateConsumed = 1;

    private readonly byte[] _value;
    private int _state;

    /// <summary>
    /// The 33-byte public key the nonce was generated for (the last part of the secret nonce; not secret).
    /// </summary>
    public CompactPubKey PublicKey { get; }

    /// <summary>
    /// Whether the nonce was consumed by a signature or cleared.
    /// </summary>
    public bool IsUsed => Volatile.Read(ref _state) != StateLive;

    /// <summary>
    /// Copies a 97-byte secret nonce. The caller should zero its own copy afterwards.
    /// </summary>
    /// <exception cref="ArgumentException">Not 97 bytes, or the embedded public key does not start with 02 or 03.</exception>
    public MusigSecretNonce(ReadOnlySpan<byte> value)
    {
        if (value.Length != MusigConstants.SecretNonceLen)
            throw new ArgumentException($"A MuSig2 secret nonce must be {MusigConstants.SecretNonceLen} bytes.",
                                        nameof(value));

        PublicKey = value[MusigConstants.SecretNonceScalarsLen..];
        _value = value.ToArray();
    }

    /// <summary>
    /// Copies the 97 bytes into <paramref name="destination"/> once, then zeroes the two scalars and marks the nonce
    /// used. Throws <see cref="InvalidOperationException"/> when it was already used or cleared.
    /// </summary>
    internal void Consume(Span<byte> destination)
    {
        if (destination.Length < MusigConstants.SecretNonceLen)
            throw new ArgumentException($"The destination must hold {MusigConstants.SecretNonceLen} bytes.",
                                        nameof(destination));

        if (Interlocked.CompareExchange(ref _state, StateConsumed, StateLive) != StateLive)
            throw new InvalidOperationException("The MuSig2 secret nonce was already used.");

        _value.CopyTo(destination);
        CryptographicOperations.ZeroMemory(_value.AsSpan(0, MusigConstants.SecretNonceScalarsLen));
    }

    /// <summary>
    /// Zeroes the nonce and marks it used.
    /// </summary>
    public void Clear()
    {
        Interlocked.Exchange(ref _state, StateConsumed);
        CryptographicOperations.ZeroMemory(_value);
    }

    /// <inheritdoc cref="Clear"/>
    public void Dispose() => Clear();

    public override string ToString() => $"MusigSecretNonce(pk={PublicKey}, redacted)";
}