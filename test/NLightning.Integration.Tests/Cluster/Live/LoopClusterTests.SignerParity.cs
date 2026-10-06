using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Testing.Lnd;
using NLightning.Testing.Lnd.Signrpc;
using NLightning.Testing.Lnd.Walletrpc;
using SignKeyDescriptor = NLightning.Testing.Lnd.Signrpc.KeyDescriptor;
using SignKeyLocator = NLightning.Testing.Lnd.Signrpc.KeyLocator;
using SignTxOut = NLightning.Testing.Lnd.Signrpc.TxOut;

namespace NLightning.Integration.Tests.Cluster.Live;

using Docker.Utils;
using Domain.Crypto.Interfaces;
using LndGrpc;

public partial class LoopClusterTests
{
    private static async Task AssertSignerParityAsync(LndNodeConnection alice, NLightningTestNode node,
        LndGrpcHost host, string grpc, CancellationToken ct)
    {
        using var channel = LndGrpcChannelFactory.Create(LndSettings.FromFiles($"https://127.0.0.1:{host.BoundPort}",
            Path.Combine(grpc, "tls.cert"), Path.Combine(grpc, "admin.macaroon")));
        var ours = new Signer.SignerClient(channel);
        var own = await new WalletKit.WalletKitClient(channel).DeriveNextKeyAsync(new KeyReq { KeyFamily = 99 }, cancellationToken: ct);
        var other = await alice.WalletKitClient.DeriveNextKeyAsync(new KeyReq { KeyFamily = 99 }, cancellationToken: ct);
        static SignKeyDescriptor Locator(SignKeyDescriptor key) => new()
        {
            KeyLoc = new SignKeyLocator { KeyFamily = key.KeyLoc.KeyFamily, KeyIndex = key.KeyLoc.KeyIndex }
        };
        var ownShared = await ours.DeriveSharedKeyAsync(new SharedKeyRequest
        { KeyDesc = Locator(own), EphemeralPubkey = other.RawKeyBytes }, cancellationToken: ct);
        var lndShared = await alice.SignClient.DeriveSharedKeyAsync(new SharedKeyRequest
        { KeyDesc = Locator(other), EphemeralPubkey = own.RawKeyBytes }, cancellationToken: ct);
        Assert.Equal(lndShared.SharedKey, ownShared.SharedKey);
        var verifier = node.Services.GetRequiredService<IMusig2Service>();
        foreach (var (signer, key) in new[] { (ours, own), (alice.SignClient, other) })
            foreach (var (method, sighash) in new[] { (0, 1u), (1, 0u), (1, 1u), (2, 0u), (3, 0u), (3, 1u) })
            {
                var pubkey = new PubKey(key.RawKeyBytes.ToByteArray());
                var leafScript = new Script(Op.GetPushOp(pubkey.ToBytes()[1..]), OpcodeType.OP_CHECKSIG);
                var leaf = new TapScript(leafScript, (TapLeafVersion)0xc0);
                var outputScript = method == 0 ? pubkey.WitHash.ScriptPubKey
                    : pubkey.TaprootInternalKey.GetTaprootFullPubKey(method is 2 or 3 ? leaf.LeafHash : null).ScriptPubKey;
                var output = new NBitcoin.TxOut(Money.Satoshis(10_000), outputScript);
                var tx = Network.RegTest.CreateTransaction();
                tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
                tx.Outputs.Add(Money.Satoshis(9_000), pubkey.WitHash.ScriptPubKey);
                var previous = new SignTxOut { Value = 10_000, PkScript = ByteString.CopyFrom(outputScript.ToBytes()) };
                var request = new SignReq { RawTxBytes = ByteString.CopyFrom(tx.ToBytes()) };
                request.PrevOutputs.Add(previous);
                request.SignDescs.Add(new SignDescriptor
                {
                    KeyDesc = method == 0 ? Locator(key) : new SignKeyDescriptor { RawKeyBytes = key.RawKeyBytes },
                    InputIndex = 0,
                    SignMethod = (SignMethod)method,
                    Sighash = sighash,
                    Output = previous,
                    WitnessScript = ByteString.CopyFrom(method == 0 ? pubkey.Hash.ScriptPubKey.ToBytes()
                        : method == 3 ? leafScript.ToBytes() : []),
                    TapTweak = ByteString.CopyFrom(method == 2 ? leaf.LeafHash.ToBytes() : [])
                });
                var signed = await signer.SignOutputRawAsync(request, cancellationToken: ct);
                var signature = Assert.Single(signed.RawSigs).ToByteArray();
                if (method == 0)
                {
                    tx.Inputs[0].WitScript = new WitScript(new[] { signature.Concat(new[] { (byte)sighash }).ToArray(), pubkey.ToBytes() });
                    Assert.True(tx.CreateValidator([output]).ValidateInput(0).Error is null or ScriptError.OK);
                }
                else
                {
                    Assert.Equal(64, signature.Length);
                    var execution = method == 3 ? new TaprootExecutionData(0, leaf.LeafHash) : new TaprootExecutionData(0);
                    execution.SigHash = (TaprootSigHash)sighash;
                    Assert.True(verifier.VerifySignature(signature, method == 3 ? pubkey.ToBytes()[1..] : outputScript.ToBytes()[2..],
                        tx.GetSignatureHashTaproot([output], execution).ToBytes()));
                }
            }
        Log("LND and NLightning ECDH and six raw-signature descriptor shapes verify");
        foreach (var combineHere in new[] { false, true })
            foreach (var bip86 in new[] { false, true })
            {
                var sessionRequest = new MuSig2SessionRequest
                {
                    KeyLoc = Locator(own).KeyLoc,
                    Version = (MuSig2Version)2,
                    TaprootTweak = bip86 ? new TaprootTweakDesc { KeySpendOnly = true }
                        : new TaprootTweakDesc { ScriptRoot = ByteString.CopyFrom(new byte[32]) }
                };
                sessionRequest.AllSignerPubkeys.Add(new[] { own.RawKeyBytes, other.RawKeyBytes });
                var ourSession = await ours.MuSig2CreateSessionAsync(sessionRequest, cancellationToken: ct);
                sessionRequest.KeyLoc = Locator(other).KeyLoc;
                var theirSession = await alice.SignClient.MuSig2CreateSessionAsync(sessionRequest, cancellationToken: ct);
                Assert.Equal(theirSession.CombinedKey, ourSession.CombinedKey);
                Assert.Equal(theirSession.TaprootInternalKey, ourSession.TaprootInternalKey);
                var ownNonces = new MuSig2RegisterNoncesRequest { SessionId = ourSession.SessionId };
                ownNonces.OtherSignerPublicNonces.Add(theirSession.LocalPublicNonces);
                var theirNonces = new MuSig2RegisterNoncesRequest { SessionId = theirSession.SessionId };
                theirNonces.OtherSignerPublicNonces.Add(ourSession.LocalPublicNonces);
                Assert.True((await ours.MuSig2RegisterNoncesAsync(ownNonces, cancellationToken: ct)).HaveAllNonces);
                Assert.True((await alice.SignClient.MuSig2RegisterNoncesAsync(theirNonces, cancellationToken: ct)).HaveAllNonces);
                var digest = ByteString.CopyFrom(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
                var ourPartial = await ours.MuSig2SignAsync(new MuSig2SignRequest
                { SessionId = ourSession.SessionId, MessageDigest = digest, Cleanup = !combineHere }, cancellationToken: ct);
                var theirPartial = await alice.SignClient.MuSig2SignAsync(new MuSig2SignRequest
                { SessionId = theirSession.SessionId, MessageDigest = digest, Cleanup = combineHere }, cancellationToken: ct);
                var combine = new MuSig2CombineSigRequest { SessionId = combineHere ? ourSession.SessionId : theirSession.SessionId };
                combine.OtherPartialSignatures.Add(combineHere ? theirPartial.LocalPartialSignature : ourPartial.LocalPartialSignature);
                var result = await (combineHere ? ours : alice.SignClient).MuSig2CombineSigAsync(combine, cancellationToken: ct);
                Assert.True(result.HaveAllSignatures);
                Assert.True(verifier.VerifySignature(result.FinalSignature.ToByteArray(), ourSession.CombinedKey.ToByteArray(), digest.ToByteArray()));
            }
        Log("Mixed LND/NLightning MuSig2 combines verify in both directions with BIP86 and script-root tweaks");
    }
}