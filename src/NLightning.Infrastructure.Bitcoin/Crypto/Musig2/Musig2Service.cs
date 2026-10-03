using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Crypto.Musig2;

using Domain.Crypto.Constants;
using Domain.Crypto.Interfaces;
using Domain.Crypto.Models;
using Domain.Crypto.ValueObjects;

/// <summary>
/// <see cref="IMusig2Service"/> over the BIP 327 module <see cref="Bip327"/>: maps the Domain value objects to the
/// reference implementation's byte arrays and back. Stateless and thread-safe.
/// </summary>
internal sealed class Musig2Service : IMusig2Service
{
    /// <inheritdoc/>
    public IReadOnlyList<CompactPubKey> SortPubKeys(IEnumerable<CompactPubKey> pubKeys)
    {
        ArgumentNullException.ThrowIfNull(pubKeys);
        return Bip327.KeySort(pubKeys.Select(k => (byte[])k)).Select(k => (CompactPubKey)k).ToList();
    }

    /// <inheritdoc/>
    public MusigKeyAggregate AggregatePubKeys(IReadOnlyList<CompactPubKey> pubKeys,
                                              IReadOnlyList<MusigTweak>? tweaks = null)
    {
        ArgumentNullException.ThrowIfNull(pubKeys);
        var keys = pubKeys.ToArray();
        var tweakList = tweaks?.ToArray() ?? [];

        var internalContext = Bip327.KeyAgg(ToBytes(keys));
        var outputContext = internalContext;
        foreach (var tweak in tweakList)
            outputContext = Bip327.ApplyTweak(outputContext, tweak.Value.Span, tweak.IsXOnly);

        return new MusigKeyAggregate(keys, tweakList, Bip327.GetPlainPubKey(internalContext),
                                     Bip327.GetPlainPubKey(outputContext));
    }

    /// <inheritdoc/>
    public MusigKeyAggregate AggregateTaprootKeyPath(CompactPubKey pubKey1, CompactPubKey pubKey2)
    {
        var sorted = SortPubKeys([pubKey1, pubKey2]);
        var internalContext = Bip327.KeyAgg(ToBytes(sorted));
        var tweak = new MusigTweak(Bip327.TaprootKeyPathTweak(Bip327.GetXOnlyPubKey(internalContext)), true);
        return AggregatePubKeys(sorted, [tweak]);
    }

    /// <inheritdoc/>
    public MusigNoncePair GenerateNonce(byte[] randomness, CompactPubKey signerPubKey, PrivKey? signerPrivKey = null,
                                        byte[]? aggregateXOnlyPubKey = null, byte[]? message = null,
                                        byte[]? extraInput = null)
    {
        ArgumentNullException.ThrowIfNull(randomness);

        var (secNonce, pubNonce) = Bip327.NonceGen(randomness, signerPubKey, signerPrivKey?.Value,
                                                   aggregateXOnlyPubKey, message, extraInput);
        try
        {
            return new MusigNoncePair(new MusigSecretNonce(secNonce), pubNonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secNonce);
        }
    }

    /// <inheritdoc/>
    public MusigAggregateNonce AggregateNonces(IReadOnlyList<MusigPublicNonce> publicNonces)
    {
        ArgumentNullException.ThrowIfNull(publicNonces);
        return Bip327.NonceAgg(publicNonces.Select(n => (byte[])n).ToArray());
    }

    /// <inheritdoc/>
    public MusigPartialSignature Sign(MusigSecretNonce secretNonce, PrivKey privKey, MusigSigningSession session)
    {
        ArgumentNullException.ThrowIfNull(secretNonce);
        ArgumentNullException.ThrowIfNull(session);

        var context = ToContext(session);

        // An invalid session throws here, before the secret nonce is consumed (as BIP 327's Sign)
        Bip327.ValidateSession(context);

        var secNonce = new byte[MusigConstants.SecretNonceLen];
        try
        {
            secretNonce.Consume(secNonce);
            return Bip327.Sign(secNonce, privKey.Value, context);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secNonce);
        }
    }

    /// <inheritdoc/>
    public bool VerifyPartialSignature(MusigPartialSignature partialSignature, MusigPublicNonce publicNonce,
                                       CompactPubKey signerPubKey, MusigSigningSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Bip327.PartialSigVerifyInternal(partialSignature, publicNonce, signerPubKey, ToContext(session));
    }

    /// <inheritdoc/>
    public byte[] AggregatePartialSignatures(IReadOnlyList<MusigPartialSignature> partialSignatures,
                                             MusigSigningSession session)
    {
        ArgumentNullException.ThrowIfNull(partialSignatures);
        ArgumentNullException.ThrowIfNull(session);
        return Bip327.PartialSigAgg(partialSignatures.Select(s => (byte[])s).ToArray(), ToContext(session));
    }

    /// <inheritdoc/>
    public MusigDeterministicSignature DeterministicSign(PrivKey privKey, MusigAggregateNonce aggregateOtherNonce,
                                                         IReadOnlyList<CompactPubKey> pubKeys,
                                                         IReadOnlyList<MusigTweak> tweaks,
                                                         ReadOnlyMemory<byte> message, byte[]? randomness = null)
    {
        ArgumentNullException.ThrowIfNull(pubKeys);
        ArgumentNullException.ThrowIfNull(tweaks);

        var (pubNonce, partialSig) = Bip327.DeterministicSign(privKey.Value, aggregateOtherNonce,
                                                              ToBytes(pubKeys), ToTweaks(tweaks),
                                                              message.ToArray(), randomness);
        return new MusigDeterministicSignature(pubNonce, partialSig);
    }

    /// <inheritdoc/>
    public bool VerifySignature(byte[] signature, byte[] xOnlyPubKey, ReadOnlyMemory<byte> message)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(xOnlyPubKey);
        return Bip327.SchnorrVerify(message.Span, xOnlyPubKey, signature);
    }

    private static Bip327.SessionContext ToContext(MusigSigningSession session)
    {
        ArgumentNullException.ThrowIfNull(session.PubKeys);
        ArgumentNullException.ThrowIfNull(session.Tweaks);
        return new Bip327.SessionContext(session.AggregateNonce, ToBytes(session.PubKeys), ToTweaks(session.Tweaks),
                                         session.Message.ToArray());
    }

    private static byte[][] ToBytes(IEnumerable<CompactPubKey> pubKeys) => pubKeys.Select(k => (byte[])k).ToArray();

    private static (byte[] Tweak, bool IsXOnly)[] ToTweaks(IEnumerable<MusigTweak> tweaks) =>
        tweaks.Select(t => (t.Value.ToArray(), t.IsXOnly)).ToArray();
}