namespace NLightning.Domain.Crypto.KeyRing;

using Models;
using ValueObjects;

/// <summary>Purpose-specific swap operations. Private ring keys and secret nonces stay inside the signer.</summary>
public interface ISwapSigner
{
    Task<KeyRingKey> ResolveAsync(KeyRingLocator? locator, byte[] publicKey, CancellationToken ct);
    Task<byte[]> SharedKeyAsync(KeyRingLocator? locator, byte[] publicKey, byte[] ephemeral, CancellationToken ct);
    Task<byte[]> SignOutputAsync(byte[] rawTransaction, SwapSignDescriptor descriptor, IReadOnlyList<SwapPrevOutput> prevOutputs, CancellationToken ct);
    MusigKeyAggregate CombineKeys(IReadOnlyList<CompactPubKey> keys, IReadOnlyList<MusigTweak> tweaks, byte[]? taprootRoot);
    Task<SwapMusigSession> CreateAsync(KeyRingLocator locator, MusigKeyAggregate aggregate, IReadOnlyList<byte[]> otherNonces, CancellationToken ct);
    bool RegisterNonces(byte[] id, IReadOnlyList<byte[]> nonces);
    byte[] Sign(byte[] id, byte[] digest, bool cleanup);
    byte[]? Combine(byte[] id, IReadOnlyList<byte[]> partials);
    void Cleanup(byte[] id);
}