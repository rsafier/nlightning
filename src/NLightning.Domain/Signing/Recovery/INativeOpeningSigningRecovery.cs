namespace NLightning.Domain.Signing.Recovery;

using Crypto.ValueObjects;

public sealed record NativeOpeningReply(CompactSignature? Signature, MusigPartialSignatureWithNonce? PartialSignature);

/// <summary>Reads an already consumed initial commitment reply without performing another signing operation.</summary>
public interface INativeOpeningSigningRecovery
{
    Task<NativeOpeningReply> ReadOpeningReplyAsync(SigningWorkflow workflow);
}