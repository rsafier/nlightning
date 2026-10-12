using System.Security.Cryptography;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.KeyRing;

using Domain.Crypto.KeyRing;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;

/// <summary>Signer-side explicit locator derivation. Append-only allocation remains in the node's public ring.</summary>
internal sealed class SignerSwapKeyRing(ISecureKeyManager keys, KeyRingOptions options) : IKeyRing
{
    public Task<KeyRingKey> DeriveNextAsync(int family, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Allocate swap locators through the node's durable public key ring.");
    public Task<KeyRingKey> DeriveAsync(KeyRingLocator locator, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Check(options, locator);
        return Task.FromResult(new KeyRingKey(locator, keys.GetKeyRingPublicKey(locator.Family, locator.Index), DateTimeOffset.UnixEpoch));
    }
    public Task<KeyRingKey?> FindAsync(CompactPubKey publicKey, CancellationToken cancellationToken = default) =>
        throw new UnauthorizedAccessException("Remote swap requests require an explicit locator resolved by the node's public key ring.");
    internal static Key Open(ISecureKeyManager keys, KeyRingOptions options, KeyRingLocator locator)
    {
        Check(options, locator);
        var secret = (byte[])keys.GetKeyRingKeyAtIndex(locator.Family, locator.Index);
        try { return ExtKey.CreateFromBytes(secret).PrivateKey; }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    private static void Check(KeyRingOptions options, KeyRingLocator locator)
    {
        if (locator.Family < 10 || !options.AllowedKeyFamilies.Contains(locator.Family))
            throw new UnauthorizedAccessException("key family is not allowed for swap signing");
        if (locator.Index < 0) throw new ArgumentOutOfRangeException(nameof(locator));
    }
}