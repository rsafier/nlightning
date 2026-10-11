namespace NLightning.Domain.Crypto.Models;

using ValueObjects;

/// <summary>
/// A BIP 327 session context: what every signer of one MuSig2 signature agrees on.
/// </summary>
/// <param name="PubKeys">The signers' keys in KeyAgg order (no sorting happens here: sort first, as the simple taproot
/// channels funding key does with KeySort).</param>
/// <param name="Tweaks">The tweaks of the aggregate key, in order.</param>
/// <param name="AggregateNonce">NonceAgg of every signer's public nonce.</param>
/// <param name="Message">The message to sign, any length (a BIP 341 sighash for a transaction).</param>
public sealed record MusigSigningSession(IReadOnlyList<CompactPubKey> PubKeys,
                                         IReadOnlyList<MusigTweak> Tweaks,
                                         MusigAggregateNonce AggregateNonce,
                                         ReadOnlyMemory<byte> Message)
{
    /// <summary>
    /// The public nonces <see cref="AggregateNonce"/> was built from, when the session was created from them
    /// (<see cref="Interfaces.IMusig2Service.CreateSession"/>; NL-904 item 3). Then
    /// <see cref="Interfaces.IMusig2Service.VerifyPartialSignature"/> accepts only a signer nonce among them and checks
    /// that the aggregate is still their NonceAgg, so a partial signature is never checked against a mismatched
    /// aggregate. Null for a session built from an aggregate nonce alone (the BIP 327 vectors).
    /// </summary>
    public IReadOnlyList<MusigPublicNonce>? PublicNonces { get; init; }
}