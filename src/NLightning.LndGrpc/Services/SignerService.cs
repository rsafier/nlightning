using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc.Services;

using Domain.Crypto.KeyRing;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.KeyRing;
using Signrpc;

/// <summary>LND's swap signer subset. All private operations stay in Infrastructure.Bitcoin's isolated ring.</summary>
public sealed class SignerService(IServiceProvider services, IOptions<LndGrpcOptions> options,
                                    ILogger<SignerService> logger) : Signer.SignerBase
{
    private ISwapSigner Backend
    {
        get
        {
            CheckEnabled(options.Value);
            return services.GetRequiredService<ISwapSigner>();
        }
    }

    internal static void CheckEnabled(LndGrpcOptions value)
    {
        if (!value.EnableSigner)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "swap signing requires LndGrpc:EnableSigner"));
    }

    public override Task<SharedKeyResponse> DeriveSharedKey(SharedKeyRequest request, ServerCallContext context) =>
        Run(async () => new SharedKeyResponse
        {
            SharedKey = ByteString.CopyFrom(await Backend.SharedKeyAsync(Locator(request.KeyDesc?.KeyLoc ?? request.KeyLoc),
                request.KeyDesc?.RawKeyBytes.ToByteArray() ?? [], request.EphemeralPubkey.ToByteArray(), context.CancellationToken))
        });

    public override Task<SignResp> SignOutputRaw(SignReq request, ServerCallContext context) => Run(async () =>
    {
        var backend = Backend;
        if (request.SignDescs.Count is 0 or > 100 || request.RawTxBytes.Length > 400_000)
            throw new ArgumentException("invalid signing request size");
        var prev = request.PrevOutputs.Select(p => new SwapPrevOutput(p.Value, p.PkScript.ToByteArray())).ToList();
        var response = new SignResp();
        foreach (var descriptor in request.SignDescs)
        {
            if (descriptor.KeyDesc is null || descriptor.Output is null)
                throw new ArgumentException("key_desc and output are required");
            var value = new SwapSignDescriptor(Locator(descriptor.KeyDesc.KeyLoc), descriptor.KeyDesc.RawKeyBytes.ToByteArray(),
                descriptor.InputIndex, (int)descriptor.SignMethod, descriptor.Sighash,
                new SwapPrevOutput(descriptor.Output.Value, descriptor.Output.PkScript.ToByteArray()),
                descriptor.WitnessScript.ToByteArray(), descriptor.SingleTweak.ToByteArray(), descriptor.DoubleTweak.ToByteArray(),
                descriptor.TapTweak.ToByteArray());
            var resolved = await backend.ResolveAsync(value.Locator, value.PublicKey, context.CancellationToken);
            var signature = await backend.SignOutputAsync(request.RawTxBytes.ToByteArray(), value, prev, context.CancellationToken);
            response.RawSigs.Add(ByteString.CopyFrom(signature));
            logger.LogInformation("Swap SignOutputRaw family {Family} index {Index} txid {TxId} input {Input} sighash {Sighash}",
                resolved.Locator.Family, resolved.Locator.Index, SwapSigner.TransactionId(request.RawTxBytes.ToByteArray()), descriptor.InputIndex, descriptor.Sighash);
        }
        return response;
    });

    public override Task<MuSig2CombineKeysResponse> MuSig2CombineKeys(MuSig2CombineKeysRequest request,
                                                                      ServerCallContext context) => Run(() =>
    {
        CheckVersion(request.Version);
        var aggregate = Aggregate(request.AllSignerPubkeys, request.Tweaks, request.TaprootTweak);
        return Task.FromResult(new MuSig2CombineKeysResponse
        {
            CombinedKey = ByteString.CopyFrom(aggregate.XOnlyOutputKey),
            TaprootInternalKey = InternalKey(aggregate, request.TaprootTweak is not null),
            Version = request.Version
        });
    });

    public override Task<MuSig2SessionResponse> MuSig2CreateSession(MuSig2SessionRequest request,
                                                                    ServerCallContext context) => Run(async () =>
    {
        CheckVersion(request.Version);
        if (request.KeyLoc is null || !request.PregeneratedLocalNonce.IsEmpty)
            throw new ArgumentException("key_loc required; caller-generated secret nonces are refused");
        var session = await Backend.CreateAsync(Locator(request.KeyLoc)!.Value,
            Aggregate(request.AllSignerPubkeys, request.Tweaks, request.TaprootTweak),
            request.OtherSignerPublicNonces.Select(n => n.ToByteArray()).ToList(), context.CancellationToken);
        return new MuSig2SessionResponse
        {

            SessionId = ByteString.CopyFrom(session.Id),
            CombinedKey = ByteString.CopyFrom(session.Aggregate.XOnlyOutputKey),
            TaprootInternalKey = InternalKey(session.Aggregate, request.TaprootTweak is not null),

            LocalPublicNonces = ByteString.CopyFrom(session.PublicNonce),
            HaveAllNonces = session.HaveAllNonces,
            Version = request.Version
        };
    });

    public override Task<MuSig2RegisterNoncesResponse> MuSig2RegisterNonces(MuSig2RegisterNoncesRequest request,
                                                                           ServerCallContext context) => Run(() =>
        Task.FromResult(new MuSig2RegisterNoncesResponse
        {
            HaveAllNonces = Backend.RegisterNonces(request.SessionId.ToByteArray(), request.OtherSignerPublicNonces.Select(n => n.ToByteArray()).ToList())
        }));

    public override Task<MuSig2SignResponse> MuSig2Sign(MuSig2SignRequest request, ServerCallContext context) => Run(() =>
    {
        var signature = Backend.Sign(request.SessionId.ToByteArray(), request.MessageDigest.ToByteArray(), request.Cleanup);
        logger.LogInformation("Swap MuSig2Sign session {SessionId}", Convert.ToHexString(request.SessionId.Span));
        return Task.FromResult(new MuSig2SignResponse { LocalPartialSignature = ByteString.CopyFrom(signature) });
    });

    public override Task<MuSig2CombineSigResponse> MuSig2CombineSig(MuSig2CombineSigRequest request,
                                                                   ServerCallContext context) => Run(() =>
    {
        var signature = Backend.Combine(request.SessionId.ToByteArray(), request.OtherPartialSignatures.Select(s => s.ToByteArray()).ToList());
        return Task.FromResult(new MuSig2CombineSigResponse
        {
            HaveAllSignatures = signature is not null,
            FinalSignature = signature is null ? ByteString.Empty : ByteString.CopyFrom(signature)
        });
    });

    public override Task<MuSig2CleanupResponse> MuSig2Cleanup(MuSig2CleanupRequest request, ServerCallContext context) => Run(() =>
    {
        Backend.Cleanup(request.SessionId.ToByteArray());
        return Task.FromResult(new MuSig2CleanupResponse());
    });

    private MusigKeyAggregate Aggregate(IEnumerable<ByteString> keys, IEnumerable<TweakDesc> tweaks, TaprootTweakDesc? taproot) =>
        Backend.CombineKeys(keys.Select(k => new CompactPubKey(k.ToByteArray())).ToList(),
            tweaks.Select(t => new MusigTweak(t.Tweak.Span, t.IsXOnly)).ToList(),
            taproot is null ? null : taproot.KeySpendOnly ? [] : taproot.ScriptRoot.ToByteArray());

    // LND's pre-taproot key includes generic tweaks; without a taproot tweak the field is absent.
    private ByteString InternalKey(MusigKeyAggregate aggregate, bool taproot) => taproot
        ? ByteString.CopyFrom(Backend.CombineKeys(aggregate.PubKeys,
            aggregate.Tweaks.Take(aggregate.Tweaks.Count - 1).ToList(), null).XOnlyOutputKey)
        : ByteString.Empty;

    private static void CheckVersion(MuSig2Version version)
    {
        if ((int)version != 2)
            throw new ArgumentException("only MuSig2 V100RC2 is supported");
    }

    internal static KeyRingLocator? Locator(KeyLocator? locator) =>
        locator is null ? null : new KeyRingLocator(locator.KeyFamily, locator.KeyIndex);

    internal static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (UnauthorizedAccessException e) { throw new RpcException(new Status(StatusCode.PermissionDenied, e.Message)); }
        catch (KeyNotFoundException e) { throw new RpcException(new Status(StatusCode.NotFound, e.Message)); }
        catch (ArgumentException e) { throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message)); }
        catch (FormatException e) { throw new RpcException(new Status(StatusCode.InvalidArgument, e.Message)); }
        catch (InvalidOperationException e) { throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message)); }
    }
}