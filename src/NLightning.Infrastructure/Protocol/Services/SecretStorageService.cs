using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NLightning.Infrastructure.Protocol.Services;

using Crypto.Factories;
using Crypto.Hashes;
using Crypto.Interfaces;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Enums;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Models;
using Models;

/// <summary>
/// Provides efficient storage of per-commitment secrets
/// </summary>
public class SecretStorageService : ISecretStorageService
{
    private const ulong MaxIndex = (1UL << 48) - 1;

    private readonly StoredSecret?[] _knownSecrets = new StoredSecret?[ShachainEntry.BucketCount];
    private readonly ICryptoProvider _cryptoProvider = CryptoFactory.GetCryptoProvider();
    private IntPtr _perCommitmentSeedPtr = IntPtr.Zero;
    private readonly Dictionary<BasepointType, IntPtr> _basepointSecrets = new();

    /// <inheritdoc/>
    public bool InsertSecret(Secret secret, ulong index)
    {
        if (index > MaxIndex)
            return false;

        // Secrets are revealed in descending index order: the last one inserted always has the lowest stored index
        var lowestIndex = GetLowestStoredIndex();
        if (lowestIndex is not null && index >= lowestIndex.Value)
            return false;

        // Find the bucket for this secret
        var bucket = ShachainEntry.GetBucket(index);

        var storedSecret = new byte[CryptoConstants.SecretLen];
        var derivedSecret = new byte[CryptoConstants.SecretLen];
        // Verify this secret can derive all previously known secrets
        for (var b = 0; b < bucket; b++)
        {
            if (_knownSecrets[b] == null)
                continue;

            DeriveSecret(secret, bucket, _knownSecrets[b]!.Index, derivedSecret);

            // Compare with stored secret (copied from secure memory)
            Marshal.Copy(_knownSecrets[b]!.SecretPtr, storedSecret, 0, CryptoConstants.SecretLen);

            if (!CryptographicOperations.FixedTimeEquals(derivedSecret, storedSecret))
            {
                // Securely wipe the temporary copy
                _cryptoProvider.MemoryZero(Marshal.UnsafeAddrOfPinnedArrayElement(storedSecret, 0),
                                           CryptoConstants.SecretLen);
                _cryptoProvider.MemoryZero(Marshal.UnsafeAddrOfPinnedArrayElement(derivedSecret, 0),
                                           CryptoConstants.SecretLen);
                return false; // Secret verification failed
            }

            // Securely wipe the temporary copies
            _cryptoProvider.MemoryZero(Marshal.UnsafeAddrOfPinnedArrayElement(storedSecret, 0),
                                       CryptoConstants.SecretLen);
            _cryptoProvider.MemoryZero(Marshal.UnsafeAddrOfPinnedArrayElement(derivedSecret, 0),
                                       CryptoConstants.SecretLen);
        }

        if (_knownSecrets[bucket] != null)
        {
            // Free previous secret in this bucket if it exists
            FreeSecret(_knownSecrets[bucket]!.SecretPtr);
        }

        // Allocate secure memory for the new secret
        var securePtr = _cryptoProvider.MemoryAlloc(CryptoConstants.SecretLen);

        // Lock memory to prevent swapping
        _cryptoProvider.MemoryLock(securePtr, CryptoConstants.SecretLen);

        // Copy secret to secure memory
        Marshal.Copy(secret, 0, securePtr, CryptoConstants.SecretLen);

        // Store in the appropriate bucket
        _knownSecrets[bucket] = new StoredSecret(index, securePtr);

        return true;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown when the secret cannot be derived</exception>
    public Secret DeriveOldSecret(ulong index)
    {
        Span<byte> derivedSecret = stackalloc byte[CryptoConstants.SecretLen];
        // Try to find a base secret that can derive this one
        for (var b = 0; b < _knownSecrets.Length; b++)
        {
            if (_knownSecrets[b] == null)
                continue;

            // Check if this secret can derive the requested index
            var mask = ~((1UL << b) - 1);
            if ((index & mask) != (_knownSecrets[b]!.Index & mask))
            {
                continue;
            }

            // Found a base secret that can derive the requested one
            var baseSecret = new byte[CryptoConstants.Sha256HashLen];
            Marshal.Copy(_knownSecrets[b]!.SecretPtr, baseSecret, 0, CryptoConstants.Sha256HashLen);

            DeriveSecret(baseSecret, b, index, derivedSecret);

            // Securely wipe the temporary base secret
            _cryptoProvider
               .MemoryZero(Marshal.UnsafeAddrOfPinnedArrayElement(baseSecret, 0), CryptoConstants.Sha256HashLen);

            return new Secret(derivedSecret.ToArray()); // Success
        }

        throw new InvalidOperationException($"Cannot derive secret for index {index}");
    }

    /// <inheritdoc/>
    public void StorePerCommitmentSeed(Secret secret)
    {
        // Free existing seed if any
        if (_perCommitmentSeedPtr != IntPtr.Zero)
            FreeSecret(_perCommitmentSeedPtr);

        // Allocate secure memory for the seed
        _perCommitmentSeedPtr = _cryptoProvider.MemoryAlloc(CryptoConstants.SecretLen);
        _cryptoProvider.MemoryLock(_perCommitmentSeedPtr, CryptoConstants.SecretLen);
        Marshal.Copy(secret, 0, _perCommitmentSeedPtr, CryptoConstants.SecretLen);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown when the per-commitment seed is not stored</exception>
    public Secret GetPerCommitmentSeed()
    {
        if (_perCommitmentSeedPtr == IntPtr.Zero)
            throw new InvalidOperationException("Per-commitment seed not stored");

        var seed = new byte[CryptoConstants.SecretLen];
        Marshal.Copy(_perCommitmentSeedPtr, seed, 0, CryptoConstants.SecretLen);
        return seed;
    }

    /// <inheritdoc/>
    public void StoreBasepointPrivateKey(BasepointType type, PrivKey privKey)
    {
        // Free existing key if any
        if (_basepointSecrets.TryGetValue(type, out var existingPtr) && existingPtr != IntPtr.Zero)
            FreeSecret(existingPtr);

        // Allocate secure memory for the private key
        var securePtr = _cryptoProvider.MemoryAlloc(CryptoConstants.SecretLen);
        _cryptoProvider.MemoryLock(securePtr, CryptoConstants.SecretLen);
        Marshal.Copy(privKey, 0, securePtr, CryptoConstants.SecretLen);

        _basepointSecrets[type] = securePtr;
    }

    /// <inheritdoc/>
    public PrivKey GetBasepointPrivateKey(BasepointType type)
    {
        if (!_basepointSecrets.TryGetValue(type, out var securePtr) || securePtr == IntPtr.Zero)
            throw new InvalidOperationException($"No private key stored for basepoint {type}");

        var privKey = new byte[CryptoConstants.PrivkeyLen];
        Marshal.Copy(securePtr, privKey, 0, CryptoConstants.PrivkeyLen);
        return privKey;
    }

    /// <inheritdoc/>
    public void Load(IEnumerable<ShachainEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var entryList = entries.OrderBy(e => e.Bucket).ToList();
        var seenBuckets = new bool[ShachainEntry.BucketCount];
        foreach (var entry in entryList)
        {
            if (entry.Bucket is < 0 or >= ShachainEntry.BucketCount)
                throw new ArgumentException($"Bucket {entry.Bucket} is out of range", nameof(entries));

            if (entry.Index > MaxIndex)
                throw new ArgumentException($"Index {entry.Index} does not fit in 48 bits", nameof(entries));

            if (ShachainEntry.GetBucket(entry.Index) != entry.Bucket)
                throw new ArgumentException($"Index {entry.Index} does not belong in bucket {entry.Bucket}",
                                            nameof(entries));

            if (seenBuckets[entry.Bucket])
                throw new ArgumentException($"Bucket {entry.Bucket} appears twice", nameof(entries));

            if (((byte[]?)entry.Secret)?.Length != CryptoConstants.SecretLen)
                throw new ArgumentException($"The secret in bucket {entry.Bucket} is not {CryptoConstants.SecretLen} bytes",
                                            nameof(entries));

            seenBuckets[entry.Bucket] = true;
        }

        // Re-run the insert_secret checks: when the secret of a higher bucket was inserted, every lower bucket holding
        // a higher index was already there and had to be derivable from it
        var derivedSecret = new byte[CryptoConstants.SecretLen];
        try
        {
            foreach (var higher in entryList)
            {
                foreach (var lower in entryList)
                {
                    if (lower.Bucket >= higher.Bucket || lower.Index <= higher.Index)
                        continue;

                    DeriveSecret(higher.Secret, higher.Bucket, lower.Index, derivedSecret);
                    if (!CryptographicOperations.FixedTimeEquals(derivedSecret, lower.Secret))
                        throw new ArgumentException(
                            $"The secret in bucket {lower.Bucket} is not derivable from the one in bucket {higher.Bucket}",
                            nameof(entries));
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derivedSecret);
        }

        // All checks passed: replace the stored secrets
        FreeKnownSecrets();
        foreach (var entry in entryList)
            _knownSecrets[entry.Bucket] = new StoredSecret(entry.Index, AllocateSecret(entry.Secret));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ShachainEntry> Export()
    {
        var entries = new List<ShachainEntry>();
        for (var b = 0; b < _knownSecrets.Length; b++)
        {
            var known = _knownSecrets[b];
            if (known is null)
                continue;

            var secret = new byte[CryptoConstants.SecretLen];
            Marshal.Copy(known.SecretPtr, secret, 0, CryptoConstants.SecretLen);
            entries.Add(new ShachainEntry(b, known.Index, secret));
        }

        return entries;
    }

    private ulong? GetLowestStoredIndex()
    {
        ulong? lowest = null;
        foreach (var known in _knownSecrets)
        {
            if (known is not null && (lowest is null || known.Index < lowest.Value))
                lowest = known.Index;
        }

        return lowest;
    }

    private IntPtr AllocateSecret(ReadOnlySpan<byte> secret)
    {
        var securePtr = _cryptoProvider.MemoryAlloc(CryptoConstants.SecretLen);
        _cryptoProvider.MemoryLock(securePtr, CryptoConstants.SecretLen);
        Marshal.Copy(secret.ToArray(), 0, securePtr, CryptoConstants.SecretLen);
        return securePtr;
    }

    private void FreeKnownSecrets()
    {
        for (var i = 0; i < _knownSecrets.Length; i++)
        {
            if (_knownSecrets[i] == null)
                continue;

            FreeSecret(_knownSecrets[i]!.SecretPtr);
            _knownSecrets[i] = null;
        }
    }

    private static void DeriveSecret(ReadOnlySpan<byte> baseSecret, int bits, ulong index, Span<byte> derivedSecret)
    {
        using var sha256 = new Sha256();

        baseSecret.CopyTo(derivedSecret);

        for (var b = bits - 1; b >= 0; b--)
        {
            if (((index >> b) & 1) == 0)
            {
                continue;
            }

            derivedSecret[b / 8] ^= (byte)(1 << (b % 8));

            sha256.AppendData(derivedSecret);
            sha256.GetHashAndReset(derivedSecret);
        }
    }

    /// <summary>
    /// Securely frees a secret from memory
    /// </summary>
    private void FreeSecret(IntPtr secretPtr)
    {
        if (secretPtr == IntPtr.Zero)
            return;

        // Wipe memory before freeing
        _cryptoProvider.MemoryZero(secretPtr, CryptoConstants.Sha256HashLen);

        // Unlock memory
        _cryptoProvider.MemoryUnlock(secretPtr, CryptoConstants.Sha256HashLen);

        // Free memory
        _cryptoProvider.MemoryFree(secretPtr);
    }

    private void ReleaseUnmanagedResources()
    {
        // Free all secrets
        FreeKnownSecrets();

        // Free per-commitment seed
        if (_perCommitmentSeedPtr != IntPtr.Zero)
        {
            FreeSecret(_perCommitmentSeedPtr);
            _perCommitmentSeedPtr = IntPtr.Zero;
        }

        // Free basepoint secrets
        foreach (var kvp in _basepointSecrets)
        {
            if (kvp.Value != IntPtr.Zero)
                FreeSecret(kvp.Value);
        }

        _basepointSecrets.Clear();
    }

    private void Dispose(bool disposing)
    {
        ReleaseUnmanagedResources();
        if (disposing)
            _cryptoProvider.Dispose();
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    ~SecretStorageService()
    {
        Dispose(false);
    }
}