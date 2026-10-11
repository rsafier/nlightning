namespace NLightning.Domain.Protocol.Interfaces;

using Crypto.ValueObjects;
using Enums;
using Models;

public interface ISecretStorageService : IDisposable
{
    /// <summary>
    /// Inserts a new secret and verifies it against existing secrets (BOLT 3 <c>insert_secret</c>).
    /// </summary>
    /// <remarks>
    /// Secrets are revealed in descending index order, so an index that is not lower than the last one inserted is
    /// rejected, as is an index above 2^48 - 1.
    /// </remarks>
    /// <param name="secret">The secret to insert</param>
    /// <param name="index">The index of the secret</param>
    /// <returns>True if the secret was inserted successfully, false otherwise</returns>
    bool InsertSecret(Secret secret, ulong index);

    /// <summary>
    /// Derives an old secret from a known higher-level secret
    /// </summary>
    /// <param name="index">The index of the secret</param>
    Secret DeriveOldSecret(ulong index);

    /// <summary>
    /// Stores the per-commitment seed securely
    /// </summary>
    /// <param name="secret">The per-commitment secret to store</param>
    void StorePerCommitmentSeed(Secret secret);

    /// <summary>
    /// Retrieves the per-commitment seed
    /// </summary>
    /// <returns>The per-commitment seed as a Secret</returns>
    Secret GetPerCommitmentSeed();

    /// <summary>
    /// Stores the private key for the specified basepoint type securely
    /// </summary>
    /// <param name="type">The type of basepoint associated with the private key</param>
    /// <param name="privKey">The private key to store securely</param>
    void StoreBasepointPrivateKey(BasepointType type, PrivKey privKey);

    /// <summary>
    /// Retrieves the private key stored for the specified basepoint type
    /// </summary>
    /// <param name="type">The basepoint type for which the private key is required</param>
    /// <returns>The private key stored with <see cref="StoreBasepointPrivateKey"/></returns>
    /// <exception cref="InvalidOperationException">No key is stored for <paramref name="type"/></exception>
    PrivKey GetBasepointPrivateKey(BasepointType type);

    /// <summary>
    /// Replaces the stored secrets with persisted buckets (from <see cref="Export"/>), after checking that each entry
    /// sits in its bucket and that the buckets are consistent with each other the way <see cref="InsertSecret"/> would
    /// have checked them.
    /// </summary>
    /// <exception cref="ArgumentException">An entry is malformed or the entries are inconsistent; nothing changes.</exception>
    void Load(IEnumerable<ShachainEntry> entries);

    /// <summary>
    /// The stored secrets, one entry per occupied bucket (ordered by bucket), for persistence.
    /// </summary>
    IReadOnlyList<ShachainEntry> Export();
}