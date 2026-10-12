using System.Security.Cryptography;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Crypto.Contexts;
using Domain.Crypto.ValueObjects;

/// <summary>
/// LND's message signatures (<c>lncli signmessage</c>/<c>verifymessage</c>, also CLN's <c>signmessage</c>,
/// NL-1162): a 65-byte recoverable compact signature over SHA256d (or SHA256) of
/// <c>"Lightning Signed Message:" || message</c>, header byte <c>27 + 4 + recovery id</c> (compressed key), then
/// <c>r || s</c>. Signing is <see cref="LocalLightningSigner.SignLightningMessage"/>; recovery needs no key.
/// </summary>
public static class LightningMessageSignature
{
    /// <summary>The prefix LND and CLN put before the signed message.</summary>
    public static readonly byte[] Prefix = "Lightning Signed Message:"u8.ToArray();

    /// <summary>The signature length.</summary>
    public const int Length = 65;

    private const byte CompressedHeaderBase = 27 + 4;

    /// <summary>The 32-byte digest of a message.</summary>
    public static byte[] Digest(ReadOnlySpan<byte> message, bool singleHash)
    {
        var prefixed = new byte[Prefix.Length + message.Length];
        Prefix.CopyTo(prefixed, 0);
        message.CopyTo(prefixed.AsSpan(Prefix.Length));
        var hash = SHA256.HashData(prefixed);
        return singleHash ? hash : SHA256.HashData(hash);
    }

    /// <summary>
    /// The key that made <paramref name="signature"/> over <paramref name="message"/> (LND's
    /// <c>ecdsa.RecoverCompact</c>: headers 27-34, compressed or not), or null when it does not recover.
    /// </summary>
    public static CompactPubKey? Recover(ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature,
                                         bool singleHash = false)
    {
        if (signature.Length != Length || signature[0] is < 27 or > 34)
            return null;

        var recoveryId = (signature[0] - 27) & 3;
        if (!SecpRecoverableECDSASignature.TryCreateFromCompact(signature[1..], recoveryId, out var recoverable)
         || recoverable is null)
            return null;

        if (!ECPubKey.TryRecover(NLightningCryptoContext.Instance, recoverable, Digest(message, singleHash),
                                 out var publicKey) || publicKey is null)
            return null;

        return new CompactPubKey(publicKey.ToBytes(true));
    }

    /// <summary>Signs <paramref name="digest"/> with <paramref name="privateKey"/> (32 bytes) as LND does.</summary>
    internal static byte[] Sign(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> digest)
    {
        if (!NLightningCryptoContext.Instance.TryCreateECPrivKey(privateKey, out var key) || key is null)
            throw new CryptographicException("The node key is not a valid secp256k1 private key");

        using (key)
        {
            // libsecp256k1: RFC 6979 nonces and low-S, as btcec's SignCompact
            if (!key.TrySignRecoverable(digest, out var recoverable) || recoverable is null)
                throw new CryptographicException("Failed to sign the message");

            var signature = new byte[Length];
            recoverable.WriteToSpanCompact(signature.AsSpan(1), out var recoveryId);
            signature[0] = (byte)(CompressedHeaderBase + recoveryId);
            return signature;
        }
    }
}