using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Crypto.Constants;

/// <summary>
/// Sizes of the BIP 327 (MuSig2 v1.0.0) values and the BIP-340 values they produce.
/// </summary>
[ExcludeFromCodeCoverage]
public static class MusigConstants
{
    /// <summary>A public nonce: two 33-byte compressed points, <c>R1 || R2</c>.</summary>
    public const int PublicNonceLen = 66;

    /// <summary>An aggregate nonce: two 33-byte points, each of which may be the point at infinity (33 zero bytes).</summary>
    public const int AggregateNonceLen = 66;

    /// <summary>A secret nonce: <c>k1 || k2 || pk</c> (32 + 32 + 33 bytes).</summary>
    public const int SecretNonceLen = 97;

    /// <summary>The two secret scalars of a secret nonce, <c>k1 || k2</c>.</summary>
    public const int SecretNonceScalarsLen = 64;

    /// <summary>A partial signature: the 32-byte <c>s</c> value.</summary>
    public const int PartialSignatureLen = 32;

    /// <summary>The simple taproot channels <c>PartialSignatureWithNonce</c>: <c>s || R1 || R2</c> (32 + 66 bytes).</summary>
    public const int PartialSignatureWithNonceLen = PartialSignatureLen + PublicNonceLen;

    /// <summary>A tweak: a 32-byte big-endian scalar.</summary>
    public const int TweakLen = 32;

    /// <summary>An x-only public key (BIP-340).</summary>
    public const int XOnlyPubKeyLen = 32;

    /// <summary>The <c>rand'</c> input of NonceGen.</summary>
    public const int NonceRandomnessLen = 32;

    /// <summary>A BIP-340 Schnorr signature.</summary>
    public const int SchnorrSignatureLen = 64;
}