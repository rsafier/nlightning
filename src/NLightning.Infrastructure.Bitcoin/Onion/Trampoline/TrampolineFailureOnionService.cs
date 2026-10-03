using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion.Trampoline;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Serialization.Interfaces;
using Infrastructure.Crypto.Ciphers;

/// <summary>
/// BOLT 4 trampoline failure packets (BOLTs PR 836): the trampoline layer under the outer layer, the intermediate
/// trampoline node's unwrap of its own route, and the origin's two-stage decryption.
/// </summary>
/// <remarks>
/// Stateless and thread-safe. Creating and wrapping go through <see cref="IFailureOnionService"/> (one packet format);
/// the loops that remove several layers are here, because they must stop on the first <c>um</c> match of one route and
/// keep the packet state between the outer and the trampoline route. Resolving the service needs
/// <see cref="IFailureMessageSerializer"/>, as <see cref="FailureOnionService"/> does.
/// </remarks>
internal sealed class TrampolineFailureOnionService : ITrampolineFailureOnionService
{
    private const int HmacLength = OnionConstants.HmacLength;

    /// <summary>
    /// The shared secret of the dummy iterations past the routes (same as <see cref="FailureOnionService"/>'s).
    /// </summary>
    private static readonly byte[] s_dummySharedSecret = new byte[CryptoConstants.SecretLen];

    private readonly IFailureOnionService _failureOnionService;
    private readonly IFailureMessageSerializer _failureMessageSerializer;

    public TrampolineFailureOnionService(IFailureOnionService failureOnionService,
                                         IFailureMessageSerializer failureMessageSerializer)
    {
        _failureOnionService = failureOnionService ?? throw new ArgumentNullException(nameof(failureOnionService));
        _failureMessageSerializer = failureMessageSerializer
                                 ?? throw new ArgumentNullException(nameof(failureMessageSerializer));
    }

    /// <inheritdoc/>
    public byte[] CreateTrampolineErrorPacket(Secret trampolineSharedSecret, Secret outerSharedSecret,
                                              FailureMessage message,
                                              int minFailurePadLength = OnionConstants.MinFailurePadLength)
    {
        ValidateSecret(outerSharedSecret, nameof(outerSharedSecret));
        var packet = _failureOnionService.CreateErrorPacket(trampolineSharedSecret, message, minFailurePadLength);
        return _failureOnionService.WrapErrorPacket(outerSharedSecret, packet);
    }

    /// <inheritdoc/>
    public byte[] WrapTrampolineErrorPacket(Secret trampolineSharedSecret, Secret outerSharedSecret,
                                            ReadOnlySpan<byte> errorPacket)
    {
        ValidateSecret(trampolineSharedSecret, nameof(trampolineSharedSecret));
        ValidateSecret(outerSharedSecret, nameof(outerSharedSecret));

        var packet = _failureOnionService.WrapErrorPacket(trampolineSharedSecret, errorPacket);
        return _failureOnionService.WrapErrorPacket(outerSharedSecret, packet);
    }

    /// <inheritdoc/>
    public TrampolineDownstreamFailure UnwrapDownstreamErrorPacket(IReadOnlyList<Secret> downstreamSharedSecrets,
                                                                   ReadOnlySpan<byte> errorPacket)
    {
        ValidateSecrets(downstreamSharedSecrets, nameof(downstreamSharedSecrets));

        // BOLT 4: a packet longer than 32768 bytes is truncated before it is transformed
        if (errorPacket.Length > OnionConstants.MaxErrorPacketLength)
            errorPacket = errorPacket[..OnionConstants.MaxErrorPacketLength];

        var packet = errorPacket.ToArray();
        var match = RemoveLayers(packet, downstreamSharedSecrets, [], 0);
        if (match is { } found)
            return TrampolineDownstreamFailure.FromOuterHop(ToDecryptedFailure(found.HopIndex, found.Payload));

        // No hop of our route authenticated it: the next trampoline node encrypted it for an earlier node
        return TrampolineDownstreamFailure.ToRewrap(packet);
    }

    /// <inheritdoc/>
    public TrampolineDecryptedFailure? DecryptTrampolineErrorPacket(IReadOnlyList<Secret> outerSharedSecrets,
                                                                    IReadOnlyList<Secret> trampolineSharedSecrets,
                                                                    ReadOnlySpan<byte> errorPacket)
    {
        ValidateSecrets(outerSharedSecrets, nameof(outerSharedSecrets));
        ValidateSecrets(trampolineSharedSecrets, nameof(trampolineSharedSecrets));

        // Too short to even hold an HMAC: nobody on either route can have authenticated it
        if (errorPacket.Length < HmacLength)
            return null;

        // BOLT 4 origin: the outer route's keys first, then the trampoline route's; keep going to a constant number
        // of iterations so the timing reveals neither route length nor the erring hop
        var routeLength = outerSharedSecrets.Count + trampolineSharedSecrets.Count;
        var iterations = Math.Max(OnionConstants.ErrorDecryptionIterations, routeLength);
        var packet = errorPacket.ToArray();
        var match = RemoveLayers(packet, outerSharedSecrets, trampolineSharedSecrets, iterations - routeLength);
        if (match is not { } found)
            return null;

        var layer = found.HopIndex < outerSharedSecrets.Count
                        ? TrampolineFailureLayer.Outer
                        : TrampolineFailureLayer.Trampoline;
        var hopIndex = layer == TrampolineFailureLayer.Outer
                           ? found.HopIndex
                           : found.HopIndex - outerSharedSecrets.Count;
        return new TrampolineDecryptedFailure(layer, ToDecryptedFailure(hopIndex, found.Payload));
    }

    /// <summary>
    /// XORs the <c>ammag</c> stream of each secret of <paramref name="first"/> then <paramref name="second"/> (then
    /// <paramref name="dummyIterations"/> constant ones) into <paramref name="packet"/> in place, checking the
    /// <c>um</c> HMAC after each; returns the first match (index over both lists, payload after the HMAC).
    /// </summary>
    private static (int HopIndex, byte[] Payload)? RemoveLayers(byte[] packet, IReadOnlyList<Secret> first,
                                                                IReadOnlyList<Secret> second, int dummyIterations)
    {
        (int HopIndex, byte[] Payload)? match = null;
        var routeLength = first.Count + second.Count;
        var iterations = routeLength + dummyIterations;

        using var keyGenerator = SphinxKeyGenerator.Rent();
        using var chaCha20 = new ChaCha20Stream();
        Span<byte> ammagKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        Span<byte> umKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        Span<byte> hmac = stackalloc byte[HmacLength];
        try
        {
            for (var i = 0; i < iterations; i++)
            {
                var sharedSecret = i < first.Count
                                       ? (ReadOnlySpan<byte>)first[i]
                                       : i < routeLength
                                           ? (ReadOnlySpan<byte>)second[i - first.Count]
                                           : s_dummySharedSecret;

                keyGenerator.DeriveKey(OnionConstants.Ammag, sharedSecret, ammagKey);
                chaCha20.Xor(ammagKey, packet, packet);

                // A packet shorter than an HMAC cannot be authenticated; keep removing layers all the same
                if (packet.Length < HmacLength)
                    continue;

                keyGenerator.DeriveKey(OnionConstants.Um, sharedSecret, umKey);
                keyGenerator.ComputeHmac(umKey, packet.AsSpan(HmacLength), ReadOnlySpan<byte>.Empty, hmac);

                var matches = CryptographicOperations.FixedTimeEquals(hmac, packet.AsSpan(0, HmacLength));
                if (matches & match is null & i < routeLength)
                    match = (i, packet[HmacLength..]);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ammagKey);
            CryptographicOperations.ZeroMemory(umKey);
        }

        return match;
    }

    private DecryptedFailure ToDecryptedFailure(int hopIndex, byte[] errorPayload)
    {
        if (!_failureMessageSerializer.TryReadErrorPayload(errorPayload, out var rawMessage))
            return new DecryptedFailure(hopIndex, ReadOnlyMemory<byte>.Empty, null);

        _failureMessageSerializer.TryDeserialize(rawMessage, out var message);
        return new DecryptedFailure(hopIndex, rawMessage, message);
    }

    private static void ValidateSecrets(IReadOnlyList<Secret> secrets, string paramName)
    {
        ArgumentNullException.ThrowIfNull(secrets, paramName);
        if (secrets.Count == 0)
            throw new ArgumentException("The route must have at least one hop.", paramName);

        foreach (var secret in secrets)
            ValidateSecret(secret, paramName);
    }

    private static void ValidateSecret(Secret secret, string paramName)
    {
        if (((ReadOnlySpan<byte>)secret).Length != CryptoConstants.SecretLen)
            throw new ArgumentException("Every shared secret must be 32 bytes.", paramName);
    }
}