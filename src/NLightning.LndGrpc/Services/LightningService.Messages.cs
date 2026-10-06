using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Infrastructure.Bitcoin.Signers;
using Lnrpc;
using Mapping;

public sealed partial class LightningService
{
    /// <summary>
    /// <c>SignMessage</c>: LND's signature of <c>msg</c> with the node key (zbase32 of the 65-byte recoverable
    /// signature over SHA256d, or SHA256 with <c>single_hash</c>, of <c>"Lightning Signed Message:" || msg</c>). The
    /// signer adds the prefix itself (<c>ILightningSigner.SignLightningMessage</c>, NL-1162).
    /// </summary>
    public override Task<SignMessageResponse> SignMessage(SignMessageRequest request, ServerCallContext context)
    {
        var signature = _signer.SignLightningMessage(request.Msg.Span, request.SingleHash);
        return Task.FromResult(new SignMessageResponse { Signature = ZBase32.Encode(signature) });
    }

    /// <summary>
    /// <c>VerifyMessage</c>: the key recovered from <c>signature</c> over <c>msg</c> (as hex) and, as LND, <c>valid</c>
    /// only when that key is a node of our graph or our own node. A signature that does not recover is
    /// <c>valid=false</c> without a key; one that is not z-base-32 is <c>INVALID_ARGUMENT</c>.
    /// </summary>
    public override Task<VerifyMessageResponse> VerifyMessage(VerifyMessageRequest request, ServerCallContext context)
    {
        byte[] signature;
        try
        {
            signature = ZBase32.Decode(request.Signature ?? string.Empty);
        }
        catch (FormatException e)
        {
            throw InvalidArgument($"failed to decode signature: {e.Message}");
        }

        if (LightningMessageSignature.Recover(request.Msg.Span, signature) is not { } publicKey)
            return Task.FromResult(new VerifyMessageResponse { Valid = false });

        var graph = Graph;
        var known = publicKey == _signer.GetNodePublicKey()
                 || (graph is not null && (graph.TryGetNode(publicKey, out _) || graph.TryGetNodeIndex(publicKey, out _)));
        return Task.FromResult(new VerifyMessageResponse { Valid = known, Pubkey = publicKey.ToString() });
    }
}