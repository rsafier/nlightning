using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion.RouteBlinding;

using Domain.Crypto.Constants;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Crypto.Ciphers;

/// <summary>
/// BOLT 4 route blinding (ONION M5) over secp256k1: <see cref="IRouteBlindingService"/>.
/// </summary>
/// <remarks>
/// The writer's path-key chain is the Sphinx ephemeral-key chain (<c>e_{i+1} = SHA256(E_i || ss_i) * e_i</c>), so it
/// reuses <see cref="OnionBuilder.ComputeHopKeys(IReadOnlyList{CompactPubKey}, PrivKey)"/>. The reader's ECDH with the
/// node key runs inside <see cref="ISecureKeyManager.ComputeNodeSharedSecret"/>; the key manager is optional so the
/// service can be resolved where none is registered (tests use <see cref="Unblind"/>). Stateless and thread-safe.
/// </remarks>
internal sealed class RouteBlindingService : IRouteBlindingService
{
    private const int TagLength = CryptoConstants.Chacha20Poly1305TagLen;

    private readonly ISecp256K1Math _secp256K1Math;
    private readonly ISecureKeyManager? _secureKeyManager;

    public RouteBlindingService(ISecp256K1Math secp256K1Math, ISecureKeyManager? secureKeyManager = null)
    {
        _secp256K1Math = secp256K1Math;
        _secureKeyManager = secureKeyManager;
    }

    /// <inheritdoc />
    public BlindedPath CreateBlindedPath(IReadOnlyList<CompactPubKey> nodeIds, IReadOnlyList<byte[]> encryptedDataTlvs,
                                         PrivKey sessionKey)
    {
        var trace = CreateBlindedPathTrace(nodeIds, encryptedDataTlvs, sessionKey);
        return new BlindedPath(nodeIds[0], trace[0].PathKey,
                               trace.Select(h => new BlindedPathHop(h.BlindedNodeId, h.EncryptedData)).ToList());
    }

    /// <summary>
    /// <see cref="CreateBlindedPath"/> with every intermediate value of each hop (path_key, shared secret, rho), for
    /// the BOLT 4 vectors.
    /// </summary>
    internal IReadOnlyList<BlindedPathHopTrace> CreateBlindedPathTrace(IReadOnlyList<CompactPubKey> nodeIds,
                                                                      IReadOnlyList<byte[]> encryptedDataTlvs,
                                                                      PrivKey sessionKey)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        ArgumentNullException.ThrowIfNull(encryptedDataTlvs);
        if (nodeIds.Count == 0)
            throw new ArgumentException("A blinded path has at least one hop.", nameof(nodeIds));
        if (encryptedDataTlvs.Count != nodeIds.Count)
            throw new ArgumentException("Every hop needs one encrypted_data_tlv.", nameof(encryptedDataTlvs));

        var (pathKeys, sharedSecrets) = OnionBuilder.ComputeHopKeys(nodeIds, sessionKey);
        var hops = new List<BlindedPathHopTrace>(nodeIds.Count);
        using var keyGenerator = SphinxKeyGenerator.Rent();
        try
        {
            for (var i = 0; i < nodeIds.Count; i++)
            {
                ArgumentNullException.ThrowIfNull(encryptedDataTlvs[i], nameof(encryptedDataTlvs));

                var rho = keyGenerator.DeriveKey(OnionConstants.Rho, sharedSecrets[i]);
                var tweak = keyGenerator.DeriveKey(OnionConstants.BlindedNodeId, sharedSecrets[i]);
                var blindedNodeId = _secp256K1Math.MultiplyPubKey(nodeIds[i], tweak);
                CryptographicOperations.ZeroMemory(tweak);

                var encrypted = new byte[encryptedDataTlvs[i].Length + TagLength];
                using (var aead = new ChaCha20Poly1305())
                {
                    aead.Encrypt(rho, 0, ReadOnlySpan<byte>.Empty, encryptedDataTlvs[i], encrypted);
                }

                hops.Add(new BlindedPathHopTrace(new CompactPubKey(pathKeys[i]), new Secret(sharedSecrets[i]),
                                                 rho, blindedNodeId, encrypted));
            }
        }
        catch
        {
            foreach (var sharedSecret in sharedSecrets)
                CryptographicOperations.ZeroMemory(sharedSecret);
            throw;
        }

        return hops;
    }

    /// <inheritdoc />
    public BlindedHopUnblinding UnblindAsLocalNode(CompactPubKey pathKey, ReadOnlyMemory<byte> encryptedRecipientData,
                                                   Secret? pathKeySharedSecret = null)
    {
        if (pathKeySharedSecret is { } known)
            return Unblind(pathKey, encryptedRecipientData, known);

        if (_secureKeyManager is null)
            throw new InvalidOperationException("No secure key manager is available to unblind with the node key.");

        return Unblind(pathKey, encryptedRecipientData,
                       ComputeSharedSecret(pathKey, _secureKeyManager.ComputeNodeSharedSecret));
    }

    /// <inheritdoc />
    public BlindedHopUnblinding Unblind(PrivKey nodeKey, CompactPubKey pathKey,
                                        ReadOnlyMemory<byte> encryptedRecipientData)
    {
        using var ecNodeKey = SphinxKeyGenerator.CreatePrivateKey(nodeKey.Value, nameof(nodeKey));
        return Unblind(pathKey, encryptedRecipientData,
                       ComputeSharedSecret(pathKey,
                                           (publicKey, output) =>
                                               SphinxKeyGenerator.ComputeEcdhSharedSecret(
                                                   ecNodeKey, publicKey, output)));
    }

    /// <summary>
    /// The blinded private key <c>b_i = HMAC256("blinded_node_id", ss_i) * k_i</c> of a node for a path_key (BOLT 4
    /// vectors; the peeler never materializes it: it tweaks the onion's ephemeral key instead).
    /// </summary>
    internal PrivKey ComputeBlindedPrivKey(PrivKey nodeKey, CompactPubKey pathKey)
    {
        using var ecNodeKey = SphinxKeyGenerator.CreatePrivateKey(nodeKey.Value, nameof(nodeKey));
        var sharedSecret = new byte[CryptoConstants.SecretLen];
        SphinxKeyGenerator.ComputeEcdhSharedSecret(ecNodeKey, pathKey, sharedSecret);
        using var keyGenerator = SphinxKeyGenerator.Rent();
        var tweak = keyGenerator.DeriveKey(OnionConstants.BlindedNodeId, sharedSecret);
        return _secp256K1Math.MultiplyPrivKey(nodeKey, tweak);
    }

    /// <summary>
    /// The derived next path_key <c>E_{i+1} = SHA256(E_i || ss_i) * E_i</c>, ignoring any override (BOLT 4 vectors).
    /// </summary>
    internal CompactPubKey DeriveNextPathKey(CompactPubKey pathKey, Secret sharedSecret)
    {
        using var keyGenerator = SphinxKeyGenerator.Rent();
        return ComputeNextPathKey(keyGenerator, pathKey, sharedSecret);
    }

    /// <inheritdoc />
    public BlindedRecipientData DecodeRecipientData(ReadOnlySpan<byte> encryptedDataTlv) =>
        BlindedRecipientDataCodec.Decode(encryptedDataTlv);

    /// <inheritdoc />
    public byte[] EncodeRecipientData(BlindedRecipientData recipientData) =>
        BlindedRecipientDataCodec.Encode(recipientData);

    private static Secret ComputeSharedSecret(CompactPubKey pathKey, OnionPeeler.NodeEcdh nodeEcdh)
    {
        if (!SphinxKeyGenerator.IsValidPublicKey(pathKey))
            throw Fail("The path_key is not a valid point.");

        var sharedSecret = new byte[CryptoConstants.SecretLen];
        nodeEcdh(pathKey, sharedSecret);
        return new Secret(sharedSecret);
    }

    private BlindedHopUnblinding Unblind(CompactPubKey pathKey, ReadOnlyMemory<byte> encryptedRecipientData,
                                         Secret sharedSecret)
    {
        // NL-077: the path_key (in particular a payload's current_path_key) must be a point on the curve
        if (!SphinxKeyGenerator.IsValidPublicKey(pathKey))
            throw Fail("The path_key is not a valid point.");

        if (encryptedRecipientData.Length < TagLength)
            throw Fail("encrypted_recipient_data is shorter than its authentication tag.");

        using var keyGenerator = SphinxKeyGenerator.Rent();
        var rho = keyGenerator.DeriveKey(OnionConstants.Rho, sharedSecret);
        var plaintext = new byte[encryptedRecipientData.Length - TagLength];
        try
        {
            using var aead = new ChaCha20Poly1305();
            aead.Decrypt(rho, 0, ReadOnlySpan<byte>.Empty, encryptedRecipientData.Span, plaintext);
        }
        catch (CryptographicException e)
        {
            throw new OnionException(FailureCode.InvalidOnionBlinding, "encrypted_recipient_data does not decrypt.",
                                     e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rho);
        }

        var recipientData = BlindedRecipientDataCodec.Decode(plaintext);
        var nextPathKey = recipientData.NextPathKeyOverride ?? ComputeNextPathKey(keyGenerator, pathKey, sharedSecret);
        return new BlindedHopUnblinding(pathKey, plaintext, recipientData, nextPathKey);
    }

    private CompactPubKey ComputeNextPathKey(SphinxKeyGenerator keyGenerator, CompactPubKey pathKey,
                                             Secret sharedSecret)
    {
        // E_{i+1} = SHA256(E_i || ss_i) * E_i
        var blindingFactor = keyGenerator.ComputeBlindingFactor(pathKey, sharedSecret);
        try
        {
            return _secp256K1Math.MultiplyPubKey(pathKey, blindingFactor);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // Only reachable if the blinding factor is 0 or >= n (negligible probability)
            throw new OnionException(FailureCode.InvalidOnionBlinding, "Failed to derive the next path_key.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blindingFactor);
        }
    }

    private static OnionException Fail(string message) => new(FailureCode.InvalidOnionBlinding, message);
}

/// <summary>
/// Every value of one hop of a blinded path as its creator computes it (BOLT 4 <c>route-blinding-test.json</c>).
/// </summary>
internal sealed record BlindedPathHopTrace(CompactPubKey PathKey, Secret SharedSecret, byte[] Rho,
                                           CompactPubKey BlindedNodeId, byte[] EncryptedData);