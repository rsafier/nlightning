using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Domain.Protocol.ValueObjects;
using Infrastructure.Crypto.Ciphers;

/// <summary>
/// BOLT 4 onion packet processing (one layer, reader side).
/// </summary>
/// <remarks>
/// Order of checks, per BOLT 4 "Onion Decryption": version, public key, (route-blinding tweak), HMAC (constant time),
/// then payload framing. The input packet is never modified. The replay check belongs to the caller and must record
/// the HMAC only after this peel succeeded (see <c>IOnionReplayCache</c>).
/// </remarks>
internal sealed class OnionPeeler
{
    /// <summary>
    /// ECDH with the processing node's private key: writes <c>SHA256(compressed(node_key * publicKey))</c> into
    /// <paramref name="sharedSecret"/>. Lets the node key stay inside its owner (e.g. the secure key manager).
    /// </summary>
    public delegate void NodeEcdh(ReadOnlySpan<byte> publicKey, Span<byte> sharedSecret);

    private readonly ISecp256K1Math _secp256K1Math;

    public OnionPeeler(ISecp256K1Math secp256K1Math)
    {
        _secp256K1Math = secp256K1Math;
    }

    /// <summary>
    /// Peels one layer of <paramref name="packet"/> with <paramref name="nodeKey"/>.
    /// </summary>
    /// <inheritdoc cref="Peel(OnionPacket, ReadOnlySpan{byte}, NodeEcdh, CompactPubKey?, int, bool)"/>
    /// <exception cref="ArgumentException">If <paramref name="nodeKey"/> is not a valid private key.</exception>
    public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                            CompactPubKey? pathKey, int minPayloadLength, bool reportAsBlinding)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minPayloadLength);

        // Parse the node key once per peel and hash with the peel's own generator (no per-ECDH allocations)
        using var ecNodeKey = SphinxKeyGenerator.CreatePrivateKey(nodeKey.Value, nameof(nodeKey));
        using var keyGenerator = new SphinxKeyGenerator();
        return Peel(keyGenerator, packet, associatedData,
                    (publicKey, sharedSecret) => keyGenerator.ComputeSharedSecret(ecNodeKey, publicKey, sharedSecret),
                    pathKey, minPayloadLength, reportAsBlinding);
    }

    /// <summary>
    /// Peels one layer of <paramref name="packet"/>, doing every node-key operation through
    /// <paramref name="nodeEcdh"/>.
    /// </summary>
    /// <param name="minPayloadLength">The minimum payload length (2 for payments, 0 for onion messages).</param>
    /// <param name="reportAsBlinding">
    /// Whether every failure must be reported as <c>invalid_onion_blinding</c> (a payment with a path_key).
    /// </param>
    /// <exception cref="OnionException">On any failure, with the BOLT 4 failure code to report.</exception>
    public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, NodeEcdh nodeEcdh,
                            CompactPubKey? pathKey, int minPayloadLength, bool reportAsBlinding)
    {
        ArgumentNullException.ThrowIfNull(nodeEcdh);
        ArgumentOutOfRangeException.ThrowIfNegative(minPayloadLength);

        using var keyGenerator = new SphinxKeyGenerator();
        return Peel(keyGenerator, packet, associatedData, nodeEcdh, pathKey, minPayloadLength, reportAsBlinding);
    }

    private PeeledOnion Peel(SphinxKeyGenerator keyGenerator, OnionPacket packet, ReadOnlySpan<byte> associatedData,
                             NodeEcdh nodeEcdh, CompactPubKey? pathKey, int minPayloadLength, bool reportAsBlinding)
    {
        try
        {
            return PeelLayer(keyGenerator, packet, associatedData, nodeEcdh, pathKey, minPayloadLength);
        }
        catch (OnionException e) when (reportAsBlinding && e.FailureCode != FailureCode.InvalidOnionBlinding)
        {
            // BOLT 4 "Returning Errors": if path_key is set in the incoming update_add_htlc, the erring node MUST
            // return invalid_onion_blinding, so that error codes do not reveal which check failed inside the path.
            // That is a BADONION code, returned unencrypted, so the framing failure's shared secret is not needed.
            if (e.SharedSecret is { } unusedSharedSecret)
                CryptographicOperations.ZeroMemory(unusedSharedSecret);

            throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionBlinding,
                           $"Onion failure inside a blinded route: {e.Message}", e);
        }
    }

    private PeeledOnion PeelLayer(SphinxKeyGenerator keyGenerator, OnionPacket packet,
                                  ReadOnlySpan<byte> associatedData, NodeEcdh nodeEcdh, CompactPubKey? pathKey,
                                  int minPayloadLength)
    {
        // 1. Version
        if (packet.Version != OnionConstants.Version)
            throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionVersion,
                           $"Unknown onion version {packet.Version}.");

        // 2. Ephemeral public key
        var ephemeralPubKey = packet.PublicKey.Span;
        if (!SphinxKeyGenerator.IsValidPublicKey(ephemeralPubKey))
            throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionKey, "Invalid onion ephemeral public key.");

        var hopPayloadsLength = packet.HopPayloadsLength;
        var hopPayloads = packet.HopPayloads.Span;
        var sharedSecret = new byte[CryptoConstants.SecretLen];
        var unwrapped = new byte[2 * hopPayloadsLength];
        Span<byte> key = stackalloc byte[CryptoConstants.Sha256HashLen];
        Span<byte> computedHmac = stackalloc byte[OnionConstants.HmacLength];
        byte[]? pathKeySharedSecret = null;
        var succeeded = false;

        try
        {
            // 3. Route blinding: the blinded node key is node_key * HMAC("blinded_node_id", ECDH(path_key, node_key)).
            // ECDH(node_key * tweak, E) = ECDH(node_key, E * tweak), so tweak E instead and never materialize the key.
            var ecdhPubKey = ephemeralPubKey;
            if (pathKey.HasValue)
            {
                pathKeySharedSecret = new byte[CryptoConstants.SecretLen];
                ecdhPubKey = TweakEphemeralKeyForBlinding(keyGenerator, packet, nodeEcdh, ephemeralPubKey,
                                                          pathKey.Value, pathKeySharedSecret);
            }

            // 4. Shared secret and HMAC check
            nodeEcdh(ecdhPubKey, sharedSecret);

            keyGenerator.DeriveKey(OnionConstants.Mu, sharedSecret, key);
            keyGenerator.ComputeHmac(key, hopPayloads, associatedData, computedHmac);
            if (!CryptographicOperations.FixedTimeEquals(computedHmac, packet.Hmac.Span))
                throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionHmac, "Onion HMAC mismatch.");

            // 5. Decrypt hop_payloads || zeros(len) with the 2*len rho stream
            hopPayloads.CopyTo(unwrapped);
            keyGenerator.DeriveKey(OnionConstants.Rho, sharedSecret, key);
            using (var chaCha20 = new ChaCha20Stream())
            {
                chaCha20.Xor(key, unwrapped, unwrapped);
            }

            // 6. Framing: bigsize(len) || payload(len) || next_hmac(32), and at least hop_payloads left over.
            // From here on the HMAC is verified, so failures carry the shared secret (invalid_onion_payload is not a
            // BADONION code and must be encrypted in update_fail_htlc).
            if (!SphinxBigSize.TryRead(unwrapped, out var payloadLength, out var lengthSize))
                throw InvalidPayload("Malformed hop payload length.", sharedSecret);

            if (payloadLength < (ulong)minPayloadLength)
                throw InvalidPayload($"Hop payload length {payloadLength} is too short.", sharedSecret);

            if (payloadLength > (ulong)hopPayloadsLength
             || lengthSize + (int)payloadLength + OnionConstants.HmacLength > hopPayloadsLength)
                throw InvalidPayload($"Hop payload length {payloadLength} exceeds the hop payloads.", sharedSecret);

            var payloadEnd = lengthSize + (int)payloadLength;
            var shiftSize = payloadEnd + OnionConstants.HmacLength;
            var payload = unwrapped.AsSpan(lengthSize, (int)payloadLength).ToArray();
            var nextHmac = unwrapped.AsSpan(payloadEnd, OnionConstants.HmacLength);

            // 7. Final hop iff next_hmac is all zero; otherwise build the packet for the next hop
            OnionPacket? nextPacket = null;
            if (nextHmac.IndexOfAnyExcept((byte)0) >= 0)
            {
                var nextEphemeralPubKey = BlindEphemeralKey(keyGenerator, packet, ephemeralPubKey, sharedSecret);
                nextPacket = new OnionPacket(OnionConstants.Version, nextEphemeralPubKey,
                                             unwrapped.AsSpan(shiftSize, hopPayloadsLength), nextHmac);
            }

            succeeded = true;
            return new PeeledOnion(payload, new Secret(sharedSecret), nextPacket,
                                   pathKeySharedSecret is null ? (Secret?)null : new Secret(pathKeySharedSecret));
        }
        finally
        {
            if (!succeeded)
            {
                CryptographicOperations.ZeroMemory(sharedSecret);
                if (pathKeySharedSecret is not null)
                    CryptographicOperations.ZeroMemory(pathKeySharedSecret);
            }

            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(computedHmac);
            CryptographicOperations.ZeroMemory(unwrapped);
        }
    }

    private byte[] TweakEphemeralKeyForBlinding(SphinxKeyGenerator keyGenerator, OnionPacket packet,
                                                NodeEcdh nodeEcdh, ReadOnlySpan<byte> ephemeralPubKey,
                                                CompactPubKey pathKey, byte[] blindingSharedSecret)
    {
        if (!SphinxKeyGenerator.IsValidPublicKey(pathKey))
            throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionBlinding, "Invalid path key.");

        Span<byte> tweak = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            nodeEcdh(pathKey, blindingSharedSecret);
            keyGenerator.DeriveKey(OnionConstants.BlindedNodeId, blindingSharedSecret, tweak);
            return _secp256K1Math.MultiplyPubKey(new CompactPubKey(ephemeralPubKey.ToArray()), tweak);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionBlinding, "Failed to blind the node key.",
                           e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tweak);
        }
    }

    private CompactPubKey BlindEphemeralKey(SphinxKeyGenerator keyGenerator, OnionPacket packet,
                                            ReadOnlySpan<byte> ephemeralPubKey, byte[] sharedSecret)
    {
        // Next E = E * SHA256(E || ss) (a point multiplication, not a scalar product of secrets)
        Span<byte> blindingFactor = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            keyGenerator.ComputeBlindingFactor(ephemeralPubKey, sharedSecret, blindingFactor);
            return _secp256K1Math.MultiplyPubKey(new CompactPubKey(ephemeralPubKey.ToArray()), blindingFactor);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // Only reachable if the blinding factor is 0 or >= n (negligible probability)
            throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionKey,
                           "Failed to blind the ephemeral public key.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blindingFactor);
        }
    }

    private static OnionException BadOnion(SphinxKeyGenerator keyGenerator, OnionPacket packet, FailureCode code,
                                           string message, Exception? innerException = null)
    {
        var sha256OfOnion = keyGenerator.ComputeSha256(packet);
        return innerException is null
                   ? new OnionException(code, message, sha256OfOnion)
                   : new OnionException(code, message, innerException, sha256OfOnion);
    }

    private static OnionException InvalidPayload(string message, byte[] sharedSecret)
    {
        // The framing failure cannot be narrowed down to a TLV: report bigsize type 0 || u16 offset 0. The HMAC has
        // verified, so hand the caller a copy of the shared secret to encrypt the failure with.
        return new OnionException(FailureCode.InvalidOnionPayload, message,
                                  InvalidOnionPayloadFailureFactory.EncodeData(new BigSize(0), 0))
        {
            SharedSecret = new Secret(sharedSecret.ToArray())
        };
    }
}