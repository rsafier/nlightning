using System.Text.Json;
using NLightning.Domain.Crypto.KeyRing;
using NLightning.Domain.Crypto.ValueObjects;

namespace NLightning.Infrastructure.RemoteSigning;

public static class SwapSignerDispatcher
{
    public static object?[] Execute(ISwapSigner signer, uint operation, JsonElement[] args) => operation switch
    {
        SwapSignerOperations.Resolve => [signer.ResolveAsync(Read<KeyRingLocator?>(args, 0), Read<byte[]>(args, 1), CancellationToken.None).GetAwaiter().GetResult()],
        SwapSignerOperations.SharedKey => [signer.SharedKeyAsync(Read<KeyRingLocator?>(args, 0), Read<byte[]>(args, 1), Read<byte[]>(args, 2), CancellationToken.None).GetAwaiter().GetResult()],
        SwapSignerOperations.SignOutput => [signer.SignOutputAsync(Read<byte[]>(args, 0), Read<SwapSignDescriptor>(args, 1), Read<IReadOnlyList<SwapPrevOutput>>(args, 2), CancellationToken.None).GetAwaiter().GetResult()],
        SwapSignerOperations.CombineKeys => [SwapAggregateWire.From(signer.CombineKeys(Read<IReadOnlyList<CompactPubKey>>(args, 0), Read<IReadOnlyList<SwapTweakWire>>(args, 1).Select(t => t.ToTweak()).ToArray(), args[2].ValueKind == JsonValueKind.Null ? null : Read<byte[]>(args, 2)))],
        SwapSignerOperations.Create => [Session(signer.CreateAsync(Read<KeyRingLocator>(args, 0), Read<SwapAggregateWire>(args, 1).ToAggregate(), Read<IReadOnlyList<byte[]>>(args, 2), CancellationToken.None).GetAwaiter().GetResult())],
        SwapSignerOperations.RegisterNonces => [signer.RegisterNonces(Read<byte[]>(args, 0), Read<IReadOnlyList<byte[]>>(args, 1))],
        SwapSignerOperations.Sign => [signer.Sign(Read<byte[]>(args, 0), Read<byte[]>(args, 1), Read<bool>(args, 2))],
        SwapSignerOperations.Combine => [signer.Combine(Read<byte[]>(args, 0), Read<IReadOnlyList<byte[]>>(args, 1))],
        SwapSignerOperations.Cleanup => Cleanup(signer, Read<byte[]>(args, 0)),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
    private static T Read<T>(JsonElement[] args, int index) => SignerWire.Read<T>(args[index]);
    private static SwapSessionWire Session(SwapMusigSession value) => new(value.Id, SwapAggregateWire.From(value.Aggregate), value.PublicNonce, value.HaveAllNonces);
    private static object?[] Cleanup(ISwapSigner signer, byte[] id) { signer.Cleanup(id); return []; }
}