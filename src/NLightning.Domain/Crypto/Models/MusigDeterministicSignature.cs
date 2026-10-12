namespace NLightning.Domain.Crypto.Models;

using ValueObjects;

/// <summary>
/// The result of BIP 327 DeterministicSign: the stateless signer's public nonce and its partial signature.
/// </summary>
public sealed record MusigDeterministicSignature(MusigPublicNonce PublicNonce, MusigPartialSignature PartialSignature);