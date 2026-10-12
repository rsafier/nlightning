using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc.Services;

using Domain.Crypto.KeyRing;
using Signrpc;
using Walletrpc;

public sealed partial class WalletKitService
{
    private IKeyRing Ring
    {
        get
        {
            SignerService.CheckEnabled(_serviceProvider.GetRequiredService<IOptions<LndGrpcOptions>>().Value);
            return _serviceProvider.GetRequiredService<IKeyRing>();
        }
    }

    public override Task<KeyDescriptor> DeriveNextKey(KeyReq request, ServerCallContext context) =>
        SignerService.Run(async () => ToDescriptor(await Ring.DeriveNextAsync(request.KeyFamily, context.CancellationToken)));

    public override Task<KeyDescriptor> DeriveKey(KeyLocator request, ServerCallContext context) =>
        SignerService.Run(async () => ToDescriptor(await Ring.DeriveAsync(new KeyRingLocator(request.KeyFamily, request.KeyIndex), context.CancellationToken)));

    private static KeyDescriptor ToDescriptor(KeyRingKey key) => new()
    {
        RawKeyBytes = ByteString.CopyFrom((byte[])key.PublicKey),
        KeyLoc = new KeyLocator { KeyFamily = key.Locator.Family, KeyIndex = key.Locator.Index }
    };
}