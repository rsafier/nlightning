namespace NLightning.Application.Payments.Onion;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Models;

/// <summary>
/// Fail the HTLC with <c>update_fail_htlc</c>: the outer onion was fine and we peeled the trampoline onion, but the
/// trampoline layer is invalid (its payload, the cross-onion checks, or, at the introduction node of a blinded
/// trampoline route, its recipient data). The reason is created with both secrets:
/// <c>ITrampolineFailureOnionService.CreateTrampolineErrorPacket(TrampolineSharedSecret, OuterSharedSecret, Failure)</c>.
/// </summary>
/// <param name="OuterSharedSecret">The shared secret of the outer (payment) onion.</param>
/// <param name="TrampolineSharedSecret">The shared secret of the trampoline layer we peeled.</param>
/// <param name="Failure">The failure to return to the origin.</param>
public sealed record IncomingOnionTrampolineFailed(Secret OuterSharedSecret, Secret TrampolineSharedSecret,
                                                   FailureMessage Failure) : IncomingOnionResult
{
    /// <inheritdoc />
    public override Secret? SharedSecretOrNull => OuterSharedSecret;
}