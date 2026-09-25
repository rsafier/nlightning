using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Crypto.Ciphers;
using Infrastructure.Crypto.Interfaces;

/// <summary>
/// BOLT 4 onion packet processing (one layer, reader side).
/// </summary>
/// <remarks>
/// Order of checks, per BOLT 4 "Onion Decryption": version, public key, (route-blinding tweak), HMAC (constant time),
/// then payload framing. The input packet is never modified.
/// </remarks>
internal sealed class OnionPeeler
{
    /// <summary>
    /// <c>invalid_onion_payload</c> data when the failure cannot be narrowed down to a TLV: bigsize type 0 || u16 offset 0.
    /// </summary>
    private static readonly byte[] s_framingFailureData = [0x00, 0x00, 0x00];

    private readonly IEcdh _ecdh;
    private readonly ISecp256K1Math _secp256K1Math;

    public OnionPeeler(IEcdh ecdh, ISecp256K1Math secp256K1Math)
    {
        _ecdh = ecdh;
        _secp256K1Math = secp256K1Math;
    }

    /// <summary>
    /// Peels one layer of <paramref name="packet"/> with <paramref name="nodeKey"/>.
    /// </summary>
    /// <exception cref="OnionException">On any failure, with the BOLT 4 failure code to report.</exception>
    public PeeledOnion Peel(OnionPacket packet, ReadOnlySpan<byte> associatedData, PrivKey nodeKey,
                            CompactPubKey? pathKey)
    {
        using var keyGenerator = new SphinxKeyGenerator();

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
        byte[]? blindedNodeKey = null;
        var succeeded = false;

        try
        {
            // 3. Route blinding: tweak the node key by HMAC("blinded_node_id", ECDH(path_key, node_key))
            var effectiveNodeKey = nodeKey;
            if (pathKey.HasValue)
            {
                blindedNodeKey = BlindNodeKey(keyGenerator, packet, nodeKey, pathKey.Value);
                effectiveNodeKey = blindedNodeKey;
            }

            // 4. Shared secret and HMAC check
            _ecdh.SecP256K1Dh(effectiveNodeKey, ephemeralPubKey, sharedSecret);

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

            // 6. Framing: bigsize(len) || payload(len) || next_hmac(32), and at least hop_payloads left over
            if (!SphinxBigSize.TryRead(unwrapped, out var payloadLength, out var lengthSize))
                throw InvalidPayload("Malformed hop payload length.");

            if (payloadLength < 2)
                throw InvalidPayload($"Hop payload length {payloadLength} is too short.");

            if (payloadLength > (ulong)hopPayloadsLength
             || lengthSize + (int)payloadLength + OnionConstants.HmacLength > hopPayloadsLength)
                throw InvalidPayload($"Hop payload length {payloadLength} exceeds the hop payloads.");

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
            return new PeeledOnion(payload, new Secret(sharedSecret), nextPacket);
        }
        finally
        {
            if (!succeeded)
                CryptographicOperations.ZeroMemory(sharedSecret);

            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(computedHmac);
            CryptographicOperations.ZeroMemory(unwrapped);
            if (blindedNodeKey is not null)
                CryptographicOperations.ZeroMemory(blindedNodeKey);
        }
    }

    private byte[] BlindNodeKey(SphinxKeyGenerator keyGenerator, OnionPacket packet, PrivKey nodeKey,
                                CompactPubKey pathKey)
    {
        if (!SphinxKeyGenerator.IsValidPublicKey(pathKey))
            throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionBlinding, "Invalid path key.");

        Span<byte> blindingSharedSecret = stackalloc byte[CryptoConstants.SecretLen];
        Span<byte> tweak = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            _ecdh.SecP256K1Dh(nodeKey, pathKey, blindingSharedSecret);
            keyGenerator.DeriveKey(OnionConstants.BlindedNodeId, blindingSharedSecret, tweak);
            return _secp256K1Math.MultiplyPrivKey(nodeKey, tweak);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            throw BadOnion(keyGenerator, packet, FailureCode.InvalidOnionBlinding, "Failed to blind the node key.",
                           e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blindingSharedSecret);
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

    private static OnionException InvalidPayload(string message)
    {
        return new OnionException(FailureCode.InvalidOnionPayload, message, s_framingFailureData.ToArray());
    }
}