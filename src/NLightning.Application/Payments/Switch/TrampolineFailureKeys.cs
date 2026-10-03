namespace NLightning.Application.Payments.Switch;

using Domain.Crypto.ValueObjects;
using Onion;

/// <summary>
/// How <see cref="HtlcSwitch"/> fails an incoming HTLC that reached us as a trampoline node (NL-875): the reason is
/// created with <see cref="TrampolineSharedSecret"/> then <see cref="OuterSharedSecret"/>, or, past the introduction
/// node of a blinded trampoline route, the HTLC is failed with <c>update_fail_malformed_htlc</c> +
/// <c>invalid_onion_blinding</c> and <see cref="BlindedMalformedSha256"/>.
/// </summary>
/// <remarks>Memory only: a replayed lock-in peels the stored onion again and rebuilds it.</remarks>
internal sealed record TrampolineFailureKeys(Secret OuterSharedSecret, Secret TrampolineSharedSecret,
                                             byte[]? BlindedMalformedSha256)
{
    public static TrampolineFailureKeys From(IncomingOnionTrampolineResult onion) =>
        new(onion.OuterSharedSecret, onion.TrampolineSharedSecret,
            onion.IsBlindedPastIntroduction ? onion.TrampolineOnionSha256 : null);
}