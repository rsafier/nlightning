using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.KeyRing;

using Domain.Crypto.KeyRing;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;

/// <summary>Append-only key allocation: the save completes before a public key is handed out.</summary>
public sealed class KeyRingService : IKeyRing, IDisposable
{
    private readonly ISecureKeyManager _keys;
    private readonly IServiceScopeFactory _scopes;
    private readonly KeyRingOptions _options;
    private readonly SemaphoreSlim _gate = new(1);

    public KeyRingService(ISecureKeyManager keys, IServiceScopeFactory scopes, IOptions<KeyRingOptions> options)
    {
        _keys = keys;
        _scopes = scopes;
        _options = options.Value;
    }

    public async Task<KeyRingKey> DeriveNextAsync(int family, CancellationToken cancellationToken = default)
    {
        Check(new KeyRingLocator(family, 0));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var last = await uow.KeyRingDbRepository.LastIndexAsync(family);
            var index = last is null ? 0 : checked(last.Value + 1);
            return await Record(uow, new KeyRingLocator(family, index), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<KeyRingKey> DeriveAsync(KeyRingLocator locator, CancellationToken cancellationToken = default)
    {
        Check(locator);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return await uow.KeyRingDbRepository.GetAsync(locator) ?? await Record(uow, locator, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<KeyRingKey?> FindAsync(CompactPubKey publicKey, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopes.CreateAsyncScope();
        cancellationToken.ThrowIfCancellationRequested();
        var key = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().KeyRingDbRepository.FindAsync(publicKey);
        if (key is not null)
            Check(key.Locator);
        return key;
    }

    internal Key Open(KeyRingLocator locator)
    {
        Check(locator);
        var bytes = (byte[])_keys.GetKeyRingKeyAtIndex(locator.Family, locator.Index);
        try { return ExtKey.CreateFromBytes(bytes).PrivateKey; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task<KeyRingKey> Record(IUnitOfWork uow, KeyRingLocator locator, CancellationToken ct)
    {
        var record = new KeyRingKey(locator, _keys.GetKeyRingPublicKey(locator.Family, locator.Index),
                                    DateTimeOffset.UtcNow);
        uow.KeyRingDbRepository.Add(record);
        ct.ThrowIfCancellationRequested();
        await uow.SaveChangesAsync();
        return record;
    }

    private void Check(KeyRingLocator locator)
    {
        if (locator.Family < 10 || !_options.AllowedKeyFamilies.Contains(locator.Family))
            throw new UnauthorizedAccessException("key family is not allowed for swap signing");
        if (locator.Index < 0)
            throw new ArgumentOutOfRangeException(nameof(locator), "key index must be nonnegative");
    }

    public void Dispose() => _gate.Dispose();
}