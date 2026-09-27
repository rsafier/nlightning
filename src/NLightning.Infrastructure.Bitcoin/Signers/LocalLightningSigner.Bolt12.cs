using System.Security.Cryptography;
using NBitcoin.Secp256k1;
using HMACSHA256 = System.Security.Cryptography.HMACSHA256;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Crypto.Contexts;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Offers.Enums;
using Domain.Offers.Models;
using Domain.Protocol.Onion.Constants;
using Offers;
using Onion;

/// <summary>
/// BOLT 12 keys (plan B1-T2, §3.6): the node key, the transient payer keys and our blinded keys sign BIP-340 over the
/// tagged Merkle root here, so none of their secrets leaves the signer.
/// </summary>
public partial class LocalLightningSigner
{
    // offers_secret = HMAC-SHA256(node_key, OffersSecretLabel); payer_key = HMAC-SHA256(offers_secret,
    // PayerKeyLabel || invreq_metadata), BOLT 12 plan §3.6 (D3)
    private static ReadOnlySpan<byte> OffersSecretLabel => "nltg_bolt12"u8;
    private static ReadOnlySpan<byte> PayerKeyLabel => "nltg_bolt12_payer"u8;

    // BIP-340 auxiliary randomness: 32 zero bytes, as CLN (libsecp256k1 with no aux data), so a signature is
    // deterministic and reproduces the BOLT 12 signature-test.json vector
    private static readonly ReadOnlyMemory<byte> s_zeroAuxRandomness = new byte[32];

    /// <inheritdoc />
    public CompactPubKey GetBolt12PayerId(ReadOnlyMemory<byte> invoiceRequestMetadata)
    {
        if (invoiceRequestMetadata.IsEmpty)
            throw new ArgumentException("invreq_metadata must not be empty.", nameof(invoiceRequestMetadata));

        using var payerKey = DeriveBolt12PayerKey(invoiceRequestMetadata.Span);
        var pubKey = new byte[CryptoConstants.CompactPubkeyLen];
        payerKey.CreatePubKey().WriteToSpan(true, pubKey, out _);
        return pubKey;
    }

    /// <inheritdoc />
    public byte[] SignBolt12(Bolt12SigningKey key, string tag, Hash merkleRoot)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Bolt12TaggedHash.IsSignatureTag(tag))
            throw new ArgumentException($"'{tag}' is not a BOLT 12 signature tag.", nameof(tag));

        var root = (byte[])merkleRoot ?? throw new ArgumentException("No Merkle root.", nameof(merkleRoot));
        var digest = Bolt12TaggedHash.Compute(tag, root);

        using var signingKey = key.Kind switch
        {
            Bolt12SigningKeyKind.Node => GetNodeEcPrivKey(),
            Bolt12SigningKeyKind.Payer => DeriveBolt12PayerKey(key.InvoiceRequestMetadata.Span),
            Bolt12SigningKeyKind.BlindedRecipient =>
                DeriveBolt12BlindedKey(key.PathKey ?? throw new ArgumentException("No path_key.", nameof(key))),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key.Kind, "Unknown BOLT 12 signing key.")
        };

        var signature = signingKey.SignBIP340(digest, s_zeroAuxRandomness);
        var bytes = new byte[CryptoConstants.MaxSignatureSize];
        signature.WriteToSpan(bytes);
        return bytes;
    }

    /// <summary>
    /// The transient payer key of <paramref name="invoiceRequestMetadata"/>; the caller disposes it.
    /// </summary>
    private ECPrivKey DeriveBolt12PayerKey(ReadOnlySpan<byte> invoiceRequestMetadata)
    {
        if (invoiceRequestMetadata.IsEmpty)
            throw new ArgumentException("invreq_metadata must not be empty.", nameof(invoiceRequestMetadata));

        Span<byte> offersSecret = stackalloc byte[CryptoConstants.SecretLen];
        var nodePrivateKey = _secureKeyManager.GetNodeKeyPair().PrivKey.Value;
        try
        {
            HMACSHA256.HashData(nodePrivateKey, OffersSecretLabel, offersSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nodePrivateKey);
        }

        // label || metadata || counter: the counter byte is appended only in the (~2^-128) case where the HMAC is not a
        // valid scalar, so every real key is HMAC(offers_secret, label || metadata)
        var data = new byte[PayerKeyLabel.Length + invoiceRequestMetadata.Length + 1];
        PayerKeyLabel.CopyTo(data);
        invoiceRequestMetadata.CopyTo(data.AsSpan(PayerKeyLabel.Length));
        Span<byte> candidate = stackalloc byte[CryptoConstants.PrivkeyLen];
        try
        {
            for (var counter = 0; counter <= byte.MaxValue; counter++)
            {
                var input = counter == 0 ? data.AsSpan(0, data.Length - 1) : data.AsSpan();
                data[^1] = (byte)counter;
                HMACSHA256.HashData(offersSecret, input, candidate);
                if (NLightningCryptoContext.Instance.TryCreateECPrivKey(candidate, out var payerKey)
                 && payerKey is not null)
                    return payerKey;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(offersSecret);
            CryptographicOperations.ZeroMemory(candidate);
        }

        throw new SignerException("Could not derive a BOLT 12 payer key", "Internal error");
    }

    /// <summary>
    /// Our blinded key for <paramref name="pathKey"/>: <c>HMAC256("blinded_node_id", ss) * node_key</c> with
    /// <c>ss = ECDH(path_key, node_key)</c> (BOLT 4 route blinding); its public key is our hop's
    /// <c>blinded_node_id</c>. The caller disposes it.
    /// </summary>
    private ECPrivKey DeriveBolt12BlindedKey(CompactPubKey pathKey)
    {
        using var nodeKey = GetNodeEcPrivKey();
        Span<byte> sharedSecret = stackalloc byte[CryptoConstants.SecretLen];
        Span<byte> tweak = stackalloc byte[CryptoConstants.SecretLen];
        try
        {
            SphinxKeyGenerator.ComputeEcdhSharedSecret(nodeKey, (byte[])pathKey, sharedSecret);
            HMACSHA256.HashData(OnionConstants.BlindedNodeId, sharedSecret, tweak);
            return nodeKey.TweakMul(tweak);
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException("The path_key is not a valid point.", nameof(pathKey), e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(tweak);
        }
    }

    /// <summary>
    /// The node key as a secp256k1 key; the key manager's copy is wiped. The caller disposes the result.
    /// </summary>
    private ECPrivKey GetNodeEcPrivKey()
    {
        var privateKey = _secureKeyManager.GetNodeKeyPair().PrivKey.Value;
        try
        {
            if (!NLightningCryptoContext.Instance.TryCreateECPrivKey(privateKey, out var ecPrivKey)
             || ecPrivKey is null)
                throw new SignerException("The node key is not a valid secp256k1 private key", "Internal error");

            return ecPrivKey;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}