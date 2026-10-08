using NLightning.Domain.Crypto.KeyRing;
using NLightning.Domain.Crypto.Models;
using NLightning.Domain.Crypto.ValueObjects;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>Native swap signer adapter. Only public nonce and signing results cross the connection.</summary>
public sealed class RemoteSwapSigner(RemoteSignerConnection connection, IKeyRing ring) : ISwapSigner
{
    public async Task<KeyRingKey> ResolveAsync(KeyRingLocator? locator, byte[] publicKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var local = (locator is { } value ? await ring.DeriveAsync(value, ct)
            : publicKey.Length == 33 ? await ring.FindAsync(new CompactPubKey(publicKey), ct)
            : null) ?? throw new UnauthorizedAccessException("an explicit ring key locator or known public key is required");
        if (publicKey.Length != 0 && !((byte[])local.PublicKey).AsSpan().SequenceEqual(publicKey))
            throw new ArgumentException("public key does not match key locator");
        return Read<KeyRingKey>(SwapSignerOperations.Resolve, local.Locator, publicKey);
    }
    public async Task<byte[]> SharedKeyAsync(KeyRingLocator? locator, byte[] publicKey, byte[] ephemeral, CancellationToken ct)
    {
        var resolved = await ResolveAsync(locator, publicKey, ct);
        return Read<byte[]>(SwapSignerOperations.SharedKey, resolved.Locator, publicKey, ephemeral);
    }
    public async Task<byte[]> SignOutputAsync(byte[] rawTransaction, SwapSignDescriptor descriptor, IReadOnlyList<SwapPrevOutput> prevOutputs, CancellationToken ct)
    {
        var resolved = await ResolveAsync(descriptor.Locator, descriptor.PublicKey, ct);
        return Read<byte[]>(SwapSignerOperations.SignOutput, rawTransaction, descriptor with { Locator = resolved.Locator }, prevOutputs);
    }
    public MusigKeyAggregate CombineKeys(IReadOnlyList<CompactPubKey> keys, IReadOnlyList<MusigTweak> tweaks, byte[]? taprootRoot) =>
        Read<SwapAggregateWire>(SwapSignerOperations.CombineKeys, keys, tweaks.Select(SwapTweakWire.From).ToArray(), taprootRoot).ToAggregate();
    public Task<SwapMusigSession> CreateAsync(KeyRingLocator locator, MusigKeyAggregate aggregate, IReadOnlyList<byte[]> otherNonces, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = Read<SwapSessionWire>(SwapSignerOperations.Create, locator, SwapAggregateWire.From(aggregate), otherNonces);
        return Task.FromResult(new SwapMusigSession(result.Id, result.Aggregate.ToAggregate(), result.PublicNonce, result.HaveAllNonces));
    }
    public bool RegisterNonces(byte[] id, IReadOnlyList<byte[]> nonces) => Read<bool>(SwapSignerOperations.RegisterNonces, id, nonces);
    public byte[] Sign(byte[] id, byte[] digest, bool cleanup) => Read<byte[]>(SwapSignerOperations.Sign, id, digest, cleanup);
    public byte[]? Combine(byte[] id, IReadOnlyList<byte[]> partials)
    {
        var result = connection.Invoke(SwapSignerOperations.Combine, id, partials)[0];
        return result.ValueKind == System.Text.Json.JsonValueKind.Null ? null : SignerWire.Read<byte[]>(result);
    }
    public void Cleanup(byte[] id) => connection.Invoke(SwapSignerOperations.Cleanup, id);
    private T Read<T>(uint operation, params object?[] args) => SignerWire.Read<T>(connection.Invoke(operation, args)[0]);
}

public sealed record SwapTweakWire(byte[] Value, bool IsXOnly)
{
    public static SwapTweakWire From(MusigTweak tweak) => new(tweak.Value.ToArray(), tweak.IsXOnly);
    public MusigTweak ToTweak() => new(Value, IsXOnly);
}
public sealed record SwapAggregateWire(IReadOnlyList<CompactPubKey> PubKeys, IReadOnlyList<SwapTweakWire> Tweaks, CompactPubKey InternalKey, CompactPubKey OutputKey)
{
    public static SwapAggregateWire From(MusigKeyAggregate aggregate) => new(aggregate.PubKeys, aggregate.Tweaks.Select(SwapTweakWire.From).ToArray(), aggregate.InternalKey, aggregate.OutputKey);
    public MusigKeyAggregate ToAggregate() => new(PubKeys, Tweaks.Select(t => t.ToTweak()).ToArray(), InternalKey, OutputKey);
}
public sealed record SwapSessionWire(byte[] Id, SwapAggregateWire Aggregate, byte[] PublicNonce, bool HaveAllNonces);