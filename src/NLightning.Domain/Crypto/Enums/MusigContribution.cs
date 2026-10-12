namespace NLightning.Domain.Crypto.Enums;

/// <summary>
/// What a party contributed to a BIP 327 (MuSig2) session that was invalid (the <c>contrib</c> of the reference
/// implementation's <c>InvalidContributionError</c>).
/// </summary>
public enum MusigContribution
{
    /// <summary>A public key that is not a valid compressed point.</summary>
    PubKey = 1,

    /// <summary>A public nonce whose halves are not both valid compressed points.</summary>
    PubNonce = 2,

    /// <summary>An aggregate nonce with a half that is neither a valid point nor the point at infinity.</summary>
    AggNonce = 3,

    /// <summary>A partial signature at or above the curve order.</summary>
    PartialSignature = 4,

    /// <summary>The aggregate of the other signers' nonces given to DeterministicSign.</summary>
    AggOtherNonce = 5
}