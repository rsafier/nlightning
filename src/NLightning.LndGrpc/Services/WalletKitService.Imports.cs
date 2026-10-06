using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.Wallet.Interfaces;
using Infrastructure.Bitcoin.Taproot;
using Infrastructure.Bitcoin.Wallet.Imports;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Walletrpc;

public sealed partial class WalletKitService
{
    public override Task<ImportTapscriptResponse> ImportTapscript(ImportTapscriptRequest request,
                                                                  ServerCallContext context) => SignerService.Run(async () =>
    {
        SignerService.CheckEnabled(_serviceProvider.GetRequiredService<IOptions<LndGrpcOptions>>().Value);
        if (request.InternalPublicKey.Length != 32 || request.CalculateSize() > 100_000)
            throw new ArgumentException("internal_public_key must be 32 bytes; import size is capped at 100000 bytes");
        var key = new TaprootInternalPubKey(request.InternalPublicKey.ToByteArray());
        Script script;
        if (request.ScriptCase == ImportTapscriptRequest.ScriptOneofCase.FullKeyOnly)
        {
            if (!request.FullKeyOnly) throw new ArgumentException("full_key_only must be true");
            script = new Script([0x51, 0x20, .. request.InternalPublicKey.ToByteArray()]);
        }
        else
        {
            byte[] root;
            switch (request.ScriptCase)
            {
                case ImportTapscriptRequest.ScriptOneofCase.FullTree:
                    root = TapscriptImport.FullTree(request.FullTree.AllLeaves.Select(l => TapscriptImport.Leaf(l.LeafVersion, l.Script.ToByteArray())).ToList());
                    break;
                case ImportTapscriptRequest.ScriptOneofCase.PartialReveal:
                    var leaf = request.PartialReveal.RevealedLeaf ?? throw new ArgumentException("revealed_leaf required");
                    root = TapscriptImport.Leaf(leaf.LeafVersion, leaf.Script.ToByteArray());
                    var proof = request.PartialReveal.FullInclusionProof;
                    if (proof.Length % 32 != 0 || proof.Length > 128 * 32)
                        throw new ArgumentException("invalid tapscript inclusion proof");
                    for (var offset = 0; offset < proof.Length; offset += 32)
                        root = TapscriptImport.Branch(root, proof.Span.Slice(offset, 32).ToArray());
                    break;
                case ImportTapscriptRequest.ScriptOneofCase.RootHashOnly:
                    root = request.RootHashOnly.ToByteArray();
                    if (root.Length != 32) throw new ArgumentException("root_hash_only must be 32 bytes");
                    break;
                default: throw new ArgumentException("tapscript tree, proof, root or full key required");
            }
            script = key.GetTaprootFullPubKey(new uint256(root)).ScriptPubKey;
        }
        var tracker = _serviceProvider.GetRequiredService<ImportedTapscriptTracker>();
        var height = _serviceProvider.GetRequiredService<IBlockchainMonitor>().LastProcessedBlockHeight;
        if (height == 0) throw new InvalidOperationException("chain monitor is not ready");
        await tracker.ImportAsync(new ImportedTapscript(script.ToBytes(), request.InternalPublicKey.ToByteArray(),
            request.ToByteArray(), height), context.CancellationToken);
        return new ImportTapscriptResponse { P2TrAddress = script.GetDestinationAddress(_network)!.ToString() };
    });
}