namespace NLightning.Domain.Crypto.KeyRing;

using ValueObjects;

/// <summary>Public-only access to the isolated swap key ring. Locators never refer to wallet or channel keys.</summary>
public interface IKeyRing
{
    Task<KeyRingKey> DeriveNextAsync(int family, CancellationToken cancellationToken = default);
    Task<KeyRingKey> DeriveAsync(KeyRingLocator locator, CancellationToken cancellationToken = default);
    Task<KeyRingKey?> FindAsync(CompactPubKey publicKey, CancellationToken cancellationToken = default);
}

/// <summary>m/1017'/0'/family'/0/index, for all networks. Reserved families 0..9 are never available.</summary>
public readonly record struct KeyRingLocator(int Family, int Index);

public sealed record KeyRingKey(KeyRingLocator Locator, CompactPubKey PublicKey, DateTimeOffset CreatedAt);

/// <summary>Append-only public records; the highest issued index is the durable family counter.</summary>
public interface IKeyRingDbRepository
{
    Task<int?> LastIndexAsync(int family);
    Task<KeyRingKey?> GetAsync(KeyRingLocator locator);
    Task<KeyRingKey?> FindAsync(CompactPubKey publicKey);
    void Add(KeyRingKey key);
}