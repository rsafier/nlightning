using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.ValueObjects;
using Infrastructure.Crypto.Ciphers;
using Infrastructure.Crypto.Interfaces;

/// <summary>
/// BOLT 4 onion packet construction (sender side), including the variable-length filler.
/// </summary>
internal sealed class OnionBuilder
{
    private readonly IEcdh _ecdh;
    private readonly ISecp256K1Math _secp256K1Math;

    public OnionBuilder(IEcdh ecdh, ISecp256K1Math secp256K1Math)
    {
        _ecdh = ecdh;
        _secp256K1Math = secp256K1Math;
    }

    /// <summary>
    /// Builds the onion packet for <paramref name="hops"/>.
    /// </summary>
    /// <param name="minPayloadLength">The minimum payload length (2 for payments, 0 for onion messages).</param>
    /// <exception cref="ArgumentException">
    /// If the route is empty, a payload is shorter than <paramref name="minPayloadLength"/>, the framed payloads
    /// exceed <paramref name="hopPayloadsLength"/>, or a key is invalid.
    /// </exception>
    public OnionPacket Build(IReadOnlyList<OnionHop> hops, PrivKey sessionKey, ReadOnlySpan<byte> associatedData,
                             int hopPayloadsLength, int minPayloadLength)
    {
        return Build(hops, sessionKey, associatedData, hopPayloadsLength, minPayloadLength, false).Packet;
    }

    /// <summary>
    /// Builds the onion packet for <paramref name="hops"/> and returns the per-hop shared secrets with it.
    /// </summary>
    /// <inheritdoc cref="Build(IReadOnlyList{OnionHop}, PrivKey, ReadOnlySpan{byte}, int, int)"/>
    public ConstructedOnion BuildWithSharedSecrets(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                   ReadOnlySpan<byte> associatedData, int hopPayloadsLength,
                                                   int minPayloadLength)
    {
        var (packet, sharedSecrets) = Build(hops, sessionKey, associatedData, hopPayloadsLength, minPayloadLength,
                                            true);
        return new ConstructedOnion(packet, sharedSecrets!.Select(secret => new Secret(secret)).ToList());
    }

    private (OnionPacket Packet, byte[][]? SharedSecrets) Build(IReadOnlyList<OnionHop> hops, PrivKey sessionKey,
                                                                ReadOnlySpan<byte> associatedData,
                                                                int hopPayloadsLength, int minPayloadLength,
                                                                bool keepSharedSecrets)
    {
        ArgumentNullException.ThrowIfNull(hops);
        if (hops.Count == 0)
            throw new ArgumentException("The route must have at least one hop.", nameof(hops));

        if (hopPayloadsLength <= 0)
            throw new ArgumentException("Hop payloads length must be positive.", nameof(hopPayloadsLength));

        ArgumentOutOfRangeException.ThrowIfNegative(minPayloadLength);

        var shiftSizes = ComputeShiftSizes(hops, hopPayloadsLength, minPayloadLength);
        var nodeIds = new CompactPubKey[hops.Count];
        for (var i = 0; i < hops.Count; i++)
            nodeIds[i] = hops[i].NodeId;

        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();

        var (ephemeralPubKeys, sharedSecrets) = ComputeHopKeys(keyGenerator, nodeIds, sessionKey);

        var mixHeader = new byte[hopPayloadsLength];
        var filler = Array.Empty<byte>();
        var hmac = new byte[OnionConstants.HmacLength];
        Span<byte> key = stackalloc byte[CryptoConstants.Sha256HashLen];
        var succeeded = false;

        try
        {
            filler = GenerateFiller(keyGenerator, chaCha20, sharedSecrets, shiftSizes, hopPayloadsLength);

            // Initial mix header: the "pad" stream keyed from the session key
            keyGenerator.DeriveKey(OnionConstants.Pad, sessionKey.Value, key);
            chaCha20.GenerateStream(key, mixHeader);

            for (var i = hops.Count - 1; i >= 0; i--)
            {
                var shiftSize = shiftSizes[i];
                var payload = hops[i].Payload.Span;

                // Right-shift the mix header by shift_size (CopyTo handles the overlap)
                mixHeader.AsSpan(0, hopPayloadsLength - shiftSize).CopyTo(mixHeader.AsSpan(shiftSize));

                // Write bigsize(len) || payload || next_hmac
                var offset = SphinxBigSize.Write((ulong)payload.Length, mixHeader);
                payload.CopyTo(mixHeader.AsSpan(offset));
                hmac.CopyTo(mixHeader.AsSpan(offset + payload.Length));

                // Obfuscate with rho
                keyGenerator.DeriveKey(OnionConstants.Rho, sharedSecrets[i], key);
                chaCha20.Xor(key, mixHeader, mixHeader);

                // The last hop's layer carries the filler in its tail
                if (i == hops.Count - 1)
                    filler.CopyTo(mixHeader.AsSpan(hopPayloadsLength - filler.Length));

                // next_hmac = HMAC(mu, hop_payloads || associated_data)
                keyGenerator.DeriveKey(OnionConstants.Mu, sharedSecrets[i], key);
                keyGenerator.ComputeHmac(key, mixHeader, associatedData, hmac);
            }

            succeeded = true;
            return (new OnionPacket(OnionConstants.Version, ephemeralPubKeys[0], mixHeader, hmac),
                    keepSharedSecrets ? sharedSecrets : null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(filler);
            if (!keepSharedSecrets || !succeeded)
            {
                foreach (var sharedSecret in sharedSecrets)
                    CryptographicOperations.ZeroMemory(sharedSecret);
            }
        }
    }

    /// <summary>
    /// Computes, for each hop, the ephemeral public key the hop will see and the shared secret with that hop.
    /// </summary>
    /// <remarks>
    /// <c>e_0 = session_key</c>; <c>ss_i = ECDH(e_i, node_i)</c>; <c>E_i = e_i * G</c>;
    /// <c>e_{i+1} = e_i * SHA256(E_i || ss_i)</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">If the route is empty, or a node id or the session key is invalid.</exception>
    public (byte[][] EphemeralPubKeys, byte[][] SharedSecrets) ComputeHopKeys(IReadOnlyList<CompactPubKey> nodeIds,
                                                                              PrivKey sessionKey)
    {
        using var keyGenerator = new SphinxKeyGenerator();
        return ComputeHopKeys(keyGenerator, nodeIds, sessionKey);
    }

    private (byte[][] EphemeralPubKeys, byte[][] SharedSecrets) ComputeHopKeys(SphinxKeyGenerator keyGenerator,
                                                                               IReadOnlyList<CompactPubKey> nodeIds,
                                                                               PrivKey sessionKey)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (nodeIds.Count == 0)
            throw new ArgumentException("The route must have at least one hop.", nameof(nodeIds));

        var ephemeralPubKeys = new byte[nodeIds.Count][];
        var sharedSecrets = new byte[nodeIds.Count][];

        Span<byte> blindingFactor = stackalloc byte[CryptoConstants.Sha256HashLen];

        var ephemeralKey = sessionKey;
        try
        {
            for (var i = 0; i < nodeIds.Count; i++)
            {
                ReadOnlySpan<byte> nodeId = nodeIds[i];
                if (!SphinxKeyGenerator.IsValidPublicKey(nodeId))
                    throw new ArgumentException($"Invalid public key for hop {i}.", nameof(nodeIds));

                CompactPubKey ephemeralPubKey;
                try
                {
                    ephemeralPubKey = _ecdh.GenerateKeyPair(ephemeralKey.Value).CompactPubKey;
                }
                catch (Exception e) when (e is not ArgumentException)
                {
                    throw new ArgumentException("Invalid session key.", nameof(sessionKey), e);
                }

                ephemeralPubKeys[i] = ephemeralPubKey;
                sharedSecrets[i] = new byte[CryptoConstants.SecretLen];
                _ecdh.SecP256K1Dh(ephemeralKey, nodeId, sharedSecrets[i]);

                if (i == nodeIds.Count - 1)
                    break;

                keyGenerator.ComputeBlindingFactor(ephemeralPubKeys[i], sharedSecrets[i], blindingFactor);
                var nextEphemeralKey = _secp256K1Math.MultiplyPrivKey(ephemeralKey, blindingFactor);
                if (i > 0)
                    CryptographicOperations.ZeroMemory(ephemeralKey.Value);

                ephemeralKey = nextEphemeralKey;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(blindingFactor);
            if (!ReferenceEquals(ephemeralKey.Value, sessionKey.Value))
                CryptographicOperations.ZeroMemory(ephemeralKey.Value);
        }

        return (ephemeralPubKeys, sharedSecrets);
    }

    /// <summary>
    /// Generates the filler: the bytes hops 0..n-2 will append (obfuscated) when they peel, so that the sender can
    /// precompute the HMAC the final hop checks.
    /// </summary>
    /// <remarks>
    /// Variable-length version: the filler grows by each hop's shift_size, not by a fixed 65 bytes.
    /// </remarks>
    private static byte[] GenerateFiller(SphinxKeyGenerator keyGenerator, ChaCha20Stream chaCha20,
                                         byte[][] sharedSecrets, int[] shiftSizes, int hopPayloadsLength)
    {
        var fillerLength = 0;
        for (var i = 0; i < shiftSizes.Length - 1; i++)
            fillerLength += shiftSizes[i];

        var filler = new byte[fillerLength];
        if (fillerLength == 0)
            return filler;

        var stream = new byte[2 * hopPayloadsLength];
        Span<byte> rhoKey = stackalloc byte[CryptoConstants.Sha256HashLen];

        try
        {
            var currentLength = 0;
            for (var i = 0; i < shiftSizes.Length - 1; i++)
            {
                // Hop i sees the filler so far at [L - currentLength, L), then appends shift_size zero bytes; both
                // regions are obfuscated with bytes [L - currentLength, L + shift_size) of its 2L-long rho stream
                var start = hopPayloadsLength - currentLength;
                currentLength += shiftSizes[i];

                var streamSpan = stream.AsSpan(0, start + currentLength);
                keyGenerator.DeriveKey(OnionConstants.Rho, sharedSecrets[i], rhoKey);
                chaCha20.GenerateStream(rhoKey, streamSpan);

                var fillerSpan = filler.AsSpan(0, currentLength);
                var keystream = streamSpan[start..];
                for (var j = 0; j < fillerSpan.Length; j++)
                    fillerSpan[j] ^= keystream[j];
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rhoKey);
            CryptographicOperations.ZeroMemory(stream);
        }

        return filler;
    }

    private static int[] ComputeShiftSizes(IReadOnlyList<OnionHop> hops, int hopPayloadsLength, int minPayloadLength)
    {
        var shiftSizes = new int[hops.Count];
        var total = 0L;
        for (var i = 0; i < hops.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(hops[i], nameof(hops));

            var payloadLength = hops[i].Payload.Length;
            if (payloadLength < minPayloadLength)
                throw new ArgumentException($"Payload of hop {i} must be at least {minPayloadLength} bytes.",
                                            nameof(hops));

            shiftSizes[i] = SphinxBigSize.GetEncodedLength((ulong)payloadLength) + payloadLength
                          + OnionConstants.HmacLength;
            total += shiftSizes[i];
            if (total > hopPayloadsLength)
                throw new ArgumentException(
                    $"The framed hop payloads ({total} bytes) do not fit in {hopPayloadsLength} bytes.", nameof(hops));
        }

        return shiftSizes;
    }
}