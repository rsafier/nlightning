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
    /// <summary>
    /// <c>ImportPublicKey</c> (NL-1186): watches the simple output script of a public key from now on, as LND: a P2WPKH
    /// (<c>WITNESS_PUBKEY_HASH</c>, a 33- or 65-byte key, compressed for the script), a nested P2WPKH
    /// (<c>NESTED_WITNESS_PUBKEY_HASH</c>) or a BIP 86 P2TR (<c>TAPROOT_PUBKEY</c>, a 32-byte x-only key). The output
    /// is watch-only like an <c>ImportTapscript</c> import (the same tracker: listed by <c>ListUnspent</c> and
    /// <c>GetTransactions</c>, never spent, reserved or booked); importing it again changes nothing.
    /// </summary>
    public override Task<ImportPublicKeyResponse> ImportPublicKey(ImportPublicKeyRequest request,
                                                                  ServerCallContext context) => SignerService.Run(async () =>
    {
        Script script;
        byte[] internalKey;
        PubKey compressed;
        switch (request.AddressType)
        {
            case AddressType.TaprootPubkey:
                if (request.PublicKey.Length != 32 || !TaprootInternalPubKey.TryCreate(request.PublicKey.ToByteArray(),
                                                                                     out var taprootKey))
                    throw new ArgumentException("a taproot public key must be a 32-byte x-only key");
                script = taprootKey.GetTaprootFullPubKey().ScriptPubKey;
                internalKey = request.PublicKey.ToByteArray();
                compressed = new PubKey([0x02, .. internalKey]);
                break;
            case AddressType.WitnessPubkeyHash or AddressType.NestedWitnessPubkeyHash:
                if (!PubKey.TryCreatePubKey(request.PublicKey.ToByteArray(), out var key))
                    throw new ArgumentException("invalid public key");
                compressed = key.Compress();
                script = request.AddressType == AddressType.WitnessPubkeyHash
                             ? compressed.WitHash.ScriptPubKey
                             : compressed.WitHash.ScriptPubKey.Hash.ScriptPubKey;
                // The import's key column holds 32 bytes: the key's x coordinate (the full request is its definition)
                internalKey = compressed.ToBytes()[1..];
                break;
            default:
                throw new ArgumentException($"address type {request.AddressType} cannot be imported");
        }

        var tracker = _serviceProvider.GetRequiredService<ImportedTapscriptTracker>();
        var height = _serviceProvider.GetRequiredService<IBlockchainMonitor>().LastProcessedBlockHeight;
        if (height == 0) throw new InvalidOperationException("chain monitor is not ready");
        await tracker.ImportAsync(new ImportedTapscript(script.ToBytes(), internalKey, request.ToByteArray(), height),
                                  context.CancellationToken);
        return new ImportPublicKeyResponse
        {
            Status = $"public key {Convert.ToHexStringLower(compressed.ToBytes())} imported"
        };
    });

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