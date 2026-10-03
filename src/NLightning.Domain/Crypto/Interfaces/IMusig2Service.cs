namespace NLightning.Domain.Crypto.Interfaces;

using Models;
using ValueObjects;

/// <summary>
/// BIP 327 (MuSig2 v1.0.0) two-round multi-signatures, as the simple taproot channels extension uses them for the
/// funding output (commitment and cooperative close signatures): KeySort, KeyAgg with tweaks, NonceGen, NonceAgg,
/// Sign, PartialSigVerify, PartialSigAgg and DeterministicSign. Implemented in Infrastructure.Bitcoin.
/// </summary>
/// <remarks>
/// <para>
/// A party's invalid value (a public key, public nonce, aggregate nonce or partial signature that does not decode)
/// throws <see cref="Exceptions.MusigInvalidContributionException"/> naming the party and the value; an argument the
/// BIP refuses throws <see cref="Exceptions.MusigException"/> with the BIP 327 reference implementation's message.
/// </para>
/// <para>
/// The order of <see cref="MusigSigningSession.PubKeys"/> matters: KeyAgg does not sort. The simple taproot channels
/// funding key is <c>KeyAgg(KeySort(pk1, pk2))</c> with the BIP 86 tweak, which
/// <see cref="AggregateTaprootKeyPath"/> builds; sign with the session <see cref="MusigKeyAggregate.CreateSession"/>
/// gives, so the keys and the tweak are the ones the output key commits to.
/// </para>
/// </remarks>
public interface IMusig2Service
{
    /// <summary>
    /// KeySort: the keys in lexicographic order of their 33-byte encodings.
    /// </summary>
    IReadOnlyList<CompactPubKey> SortPubKeys(IEnumerable<CompactPubKey> pubKeys);

    /// <summary>
    /// KeyAgg over <paramref name="pubKeys"/> in the given order (not sorted), then each tweak in order.
    /// </summary>
    MusigKeyAggregate AggregatePubKeys(IReadOnlyList<CompactPubKey> pubKeys,
                                       IReadOnlyList<MusigTweak>? tweaks = null);

    /// <summary>
    /// The simple taproot channels funding key: <c>KeyAgg(KeySort(pubKey1, pubKey2))</c>, then the BIP 86 x-only tweak
    /// <c>tagged_hash("TapTweak", x(Q))</c> (a key-path-only taproot output, no script root).
    /// <see cref="MusigKeyAggregate.InternalKey"/> is the untweaked aggregate and
    /// <see cref="MusigKeyAggregate.OutputKey"/> the funding output key.
    /// </summary>
    MusigKeyAggregate AggregateTaprootKeyPath(CompactPubKey pubKey1, CompactPubKey pubKey2);

    /// <summary>
    /// NonceGen with caller-supplied <paramref name="randomness"/> (BIP 327's <c>rand'</c>, 32 bytes).
    /// </summary>
    /// <param name="randomness">32 bytes that must never repeat for this key: fresh uniform randomness, or the simple
    /// taproot channels counter scheme's shachain leaf for a verification nonce. Not kept.</param>
    /// <param name="signerPubKey">The signer's public key (required by BIP 327 v1.0.0).</param>
    /// <param name="signerPrivKey">The signer's secret key, mixed into the randomness when given (optional
    /// hardening).</param>
    /// <param name="aggregateXOnlyPubKey">The 32-byte x-only aggregate (output) key the nonce will sign for, when known
    /// (optional).</param>
    /// <param name="message">The message to sign, when known (optional; absent differs from empty).</param>
    /// <param name="extraInput">Any extra input (optional).</param>
    MusigNoncePair GenerateNonce(byte[] randomness, CompactPubKey signerPubKey, PrivKey? signerPrivKey = null,
                                 byte[]? aggregateXOnlyPubKey = null, byte[]? message = null,
                                 byte[]? extraInput = null);

    /// <summary>
    /// NonceAgg: the aggregate of every signer's public nonce. An undecodable nonce is an invalid contribution of that
    /// signer (its index in <paramref name="publicNonces"/>).
    /// </summary>
    MusigAggregateNonce AggregateNonces(IReadOnlyList<MusigPublicNonce> publicNonces);

    /// <summary>
    /// Sign: our partial signature in <paramref name="session"/>. Consumes <paramref name="secretNonce"/>: a second
    /// call with it throws <see cref="InvalidOperationException"/> (signing twice with one nonce reveals the key). An
    /// invalid session (a bad key, aggregate nonce or tweak) throws before the nonce is consumed.
    /// </summary>
    MusigPartialSignature Sign(MusigSecretNonce secretNonce, PrivKey privKey, MusigSigningSession session);

    /// <summary>
    /// PartialSigVerify of one signer's partial signature, given its public nonce and key (which must be in
    /// <paramref name="session"/>'s keys). False for a wrong signature or a value at or above the curve order; an
    /// undecodable nonce or key is an invalid contribution (signer null).
    /// </summary>
    bool VerifyPartialSignature(MusigPartialSignature partialSignature, MusigPublicNonce publicNonce,
                                CompactPubKey signerPubKey, MusigSigningSession session);

    /// <summary>
    /// PartialSigAgg: the 64-byte BIP-340 signature for the session's output key and message. A partial signature at
    /// or above the curve order is an invalid contribution (its index). Verify the partial signatures first: the
    /// aggregate of a wrong one is a wrong signature.
    /// </summary>
    byte[] AggregatePartialSignatures(IReadOnlyList<MusigPartialSignature> partialSignatures,
                                      MusigSigningSession session);

    /// <summary>
    /// DeterministicSign: a stateless signer's nonce and partial signature, for a signer that signs last, given the
    /// aggregate of every other signer's nonce.
    /// </summary>
    MusigDeterministicSignature DeterministicSign(PrivKey privKey, MusigAggregateNonce aggregateOtherNonce,
                                                  IReadOnlyList<CompactPubKey> pubKeys,
                                                  IReadOnlyList<MusigTweak> tweaks, ReadOnlyMemory<byte> message,
                                                  byte[]? randomness = null);

    /// <summary>
    /// BIP-340 verification of a 64-byte signature under a 32-byte x-only key, for a message of any length.
    /// </summary>
    bool VerifySignature(byte[] signature, byte[] xOnlyPubKey, ReadOnlyMemory<byte> message);
}