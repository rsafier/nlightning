namespace NLightning.Application.Payments.Switch;

using Domain.Crypto.ValueObjects;
using Onion;

/// <summary>
/// How an incoming HTLC that reached us as a trampoline node is failed (NL-875): the reason is created with
/// <see cref="TrampolineSharedSecret"/> then <see cref="OuterSharedSecret"/>, or, past the introduction node of a
/// blinded trampoline route, the HTLC is failed with <c>update_fail_malformed_htlc</c> + <c>invalid_onion_blinding</c>
/// and <see cref="BlindedMalformedSha256"/>. At the introduction node of a blinded trampoline route that is not its
/// final node (a relay), every failure is replaced by our own <c>invalid_onion_blinding</c> with
/// <see cref="IntroductionSha256"/>.
/// </summary>
/// <remarks>Memory only: a replayed lock-in peels the stored onion again and rebuilds it; the failure paths outside the
/// switch rebuild it with <see cref="TrampolineHtlcFailures.ResolveKeysAsync"/> (NL-897).</remarks>
internal sealed record TrampolineFailureKeys(Secret OuterSharedSecret, Secret TrampolineSharedSecret,
                                             byte[]? BlindedMalformedSha256, byte[]? IntroductionSha256 = null)
{
    public static TrampolineFailureKeys From(IncomingOnionTrampolineResult onion) =>
        new(onion.OuterSharedSecret, onion.TrampolineSharedSecret,
            onion.IsBlindedPastIntroduction ? onion.TrampolineOnionSha256 : null,
            onion is IncomingOnionTrampolineRelay { Blinded.IsIntroduction: true } ? onion.TrampolineOnionSha256 : null);
}