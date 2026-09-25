using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Crypto.Hashes;

using Domain.Crypto.Constants;
using Factories;
using Interfaces;

public sealed class Argon2Id : IDisposable
{
    /// <summary>
    /// Salt length required by Argon2id (libsodium <c>crypto_pwhash_SALTBYTES</c>).
    /// </summary>
    public const int SaltLen = 16;

    /// <summary>
    /// Default memory limit, in bytes (64 MiB).
    /// </summary>
    public const ulong DefaultMemLimit = 64UL * 1024 * 1024;

    /// <summary>
    /// Default number of passes over memory.
    /// </summary>
    public const ulong DefaultOpsLimit = 3;

    /// <summary>
    /// Memory limit, in bytes, used by version 1 key files (64 KiB). Only use it to read legacy data.
    /// </summary>
    public const ulong LegacyMemLimit = 1 << 16;

    /// <summary>
    /// Smallest memory limit accepted, in bytes (libsodium <c>crypto_pwhash_MEMLIMIT_MIN</c>).
    /// </summary>
    public const ulong MinMemLimit = 8192;

    /// <summary>
    /// Largest memory limit accepted, in bytes (4 GiB).
    /// </summary>
    public const ulong MaxMemLimit = 4UL * 1024 * 1024 * 1024;

    /// <summary>
    /// Largest number of passes accepted.
    /// </summary>
    public const ulong MaxOpsLimit = 64;

    private readonly ICryptoProvider _cryptoProvider;

    public Argon2Id()
    {
        _cryptoProvider = CryptoFactory.GetCryptoProvider();
    }

    /// <summary>
    /// Derives a 32-byte key with the default parameters (<see cref="DefaultOpsLimit"/>,
    /// <see cref="DefaultMemLimit"/>).
    /// </summary>
    public void DeriveKeyFromPasswordAndSalt(string password, ReadOnlySpan<byte> salt, Span<byte> key)
    {
        DeriveKeyFromPasswordAndSalt(password, salt, key, DefaultOpsLimit, DefaultMemLimit);
    }

    /// <summary>
    /// Derives a 32-byte key with explicit parameters. The password is hashed as its full UTF-8 encoding on every
    /// crypto backend.
    /// </summary>
    /// <param name="password">The password.</param>
    /// <param name="salt">A <see cref="SaltLen"/>-byte salt.</param>
    /// <param name="key">The 32-byte output key.</param>
    /// <param name="opsLimit">Number of passes over memory.</param>
    /// <param name="memLimit">Memory limit in <b>bytes</b>.</param>
    public void DeriveKeyFromPasswordAndSalt(string password, ReadOnlySpan<byte> salt, Span<byte> key,
                                             ulong opsLimit, ulong memLimit)
    {
        ArgumentNullException.ThrowIfNull(password);

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            DeriveKeyFromPasswordBytesAndSalt(passwordBytes, salt, key, opsLimit, memLimit);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>
    /// Derives a 32-byte key from raw password bytes with explicit parameters.
    /// </summary>
    /// <param name="password">The password bytes; all of them are hashed.</param>
    /// <param name="salt">A <see cref="SaltLen"/>-byte salt.</param>
    /// <param name="key">The 32-byte output key.</param>
    /// <param name="opsLimit">Number of passes over memory.</param>
    /// <param name="memLimit">Memory limit in <b>bytes</b>.</param>
    public void DeriveKeyFromPasswordBytesAndSalt(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, Span<byte> key,
                                                  ulong opsLimit, ulong memLimit)
    {
        if (key.Length != CryptoConstants.PrivkeyLen)
            throw new ArgumentException($"Key must be {CryptoConstants.PrivkeyLen} bytes long", nameof(key));

        if (salt.Length != SaltLen)
            throw new ArgumentException($"Salt must be {SaltLen} bytes long", nameof(salt));

        if (opsLimit is < 1 or > MaxOpsLimit)
            throw new ArgumentOutOfRangeException(nameof(opsLimit), opsLimit,
                                                  $"Ops limit must be between 1 and {MaxOpsLimit}");

        if (memLimit is < MinMemLimit or > MaxMemLimit)
            throw new ArgumentOutOfRangeException(nameof(memLimit), memLimit,
                                                  $"Memory limit must be between {MinMemLimit} and {MaxMemLimit} bytes");

        var ret = _cryptoProvider.DeriveKeyFromPasswordUsingArgon2I(key, password, salt, opsLimit, memLimit);

        if (ret != 0)
            throw new Exception("Argon2ID key derivation failed");
    }

    public void Dispose()
    {
        _cryptoProvider.Dispose();
    }
}