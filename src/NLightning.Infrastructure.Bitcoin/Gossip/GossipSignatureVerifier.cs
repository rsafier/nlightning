using NBitcoin.Secp256k1;

namespace NLightning.Infrastructure.Bitcoin.Gossip;

using Crypto.Contexts;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Gossip.Models;

/// <summary>
/// BOLT 7 signature verification (G0-T3): a 64-byte compact ECDSA signature over a 32-byte hash (the caller's
/// double-SHA256 of the signed range) by a 33-byte compressed key, with libsecp256k1.
/// </summary>
/// <remarks>
/// High-S signatures are accepted: libsecp256k1 rejects them, but BOLT 7 lets a relaying node replace s with -s, so
/// the signature is normalized first. Anything unparsable (wrong length, r or s overflow, a key that is not a curve
/// point, a default value) is false, never an exception. Stateless and thread safe.
/// <c>LocalLightningSigner.VerifyNodeMessage</c> delegates here.
/// </remarks>
public sealed class GossipSignatureVerifier : IGossipSignatureVerifier
{
    /// <inheritdoc />
    public bool Verify(Hash messageHash, CompactSignature signature, CompactPubKey publicKey) =>
        signature is not null && VerifyCompact((byte[])messageHash, signature.Value, (byte[])publicKey);

    /// <inheritdoc />
    public bool VerifyAll(IReadOnlyList<GossipSignatureCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count == 0)
            return false;

        for (var i = 0; i < checks.Count; i++)
        {
            var check = checks[i];
            if (!Verify(check.MessageHash, check.Signature, check.PublicKey))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Verifies <paramref name="signature"/> (64-byte compact) over <paramref name="messageHash"/> (32 bytes) by
    /// <paramref name="publicKey"/> (33-byte compressed); false for any malformed input.
    /// </summary>
    internal static bool VerifyCompact(byte[]? messageHash, byte[]? signature, byte[]? publicKey)
    {
        if (messageHash is not { Length: CryptoConstants.Sha256HashLen }
         || signature is not { Length: CryptoConstants.MaxSignatureSize }
         || publicKey is not { Length: CryptoConstants.CompactPubkeyLen }
         || !SecpECDSASignature.TryCreateFromCompact(signature, out var ecdsaSignature)
         || ecdsaSignature is null
         || !ECPubKey.TryCreate(publicKey, NLightningCryptoContext.Instance, out _, out var ecPubKey)
         || ecPubKey is null)
            return false;

        // libsecp256k1 verification rejects high-S signatures, but a relayed (malleated) one is still valid
        var (r, s) = ecdsaSignature;
        if (s.IsHigh)
            ecdsaSignature = new SecpECDSASignature(r, s.Negate(), true);

        return ecPubKey.SigVerify(ecdsaSignature, messageHash);
    }
}