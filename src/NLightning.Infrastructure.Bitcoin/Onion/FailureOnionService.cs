using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Serialization.Interfaces;
using Infrastructure.Crypto.Ciphers;

/// <summary>
/// BOLT 4 legacy failure onion: create (erring node), wrap (intermediate nodes) and decrypt (origin).
/// </summary>
/// <remarks>
/// Stateless and thread-safe: every call allocates its own key generator and ChaCha20 stream. The
/// <c>failuremsg</c> encoding and the <c>failure_len || failuremsg || pad_len || pad</c> framing come from
/// <see cref="IFailureMessageSerializer"/> (implemented in Infrastructure.Serialization, which this project cannot
/// reference), so resolving this service also needs <c>AddSerializationInfrastructureServices</c>.
/// </remarks>
internal sealed class FailureOnionService : IFailureOnionService
{
    private const int HmacLength = OnionConstants.HmacLength;

    /// <summary>
    /// The shared secret used for the dummy iterations past the end of the route. Any constant works: the keys only
    /// have to cost the same to derive and use as real ones.
    /// </summary>
    private static readonly byte[] s_dummySharedSecret = new byte[CryptoConstants.SecretLen];

    private readonly IFailureMessageSerializer _failureMessageSerializer;

    public FailureOnionService(IFailureMessageSerializer failureMessageSerializer)
    {
        _failureMessageSerializer = failureMessageSerializer
                                 ?? throw new ArgumentNullException(nameof(failureMessageSerializer));
    }

    /// <inheritdoc />
    public byte[] CreateErrorPacket(Secret sharedSecret, FailureMessage message,
                                    int minFailurePadLength = OnionConstants.MinFailurePadLength)
    {
        ArgumentNullException.ThrowIfNull(message);

        var errorPayload = _failureMessageSerializer.SerializeErrorPayload(message, minFailurePadLength);
        return CreateErrorPacket(sharedSecret, errorPayload);
    }

    /// <summary>
    /// Builds a return packet from an already framed body (<c>failure_len || failuremsg || pad_len || pad</c>):
    /// <c>ammag XOR (HMAC(um, body) || body)</c>.
    /// </summary>
    /// <exception cref="ArgumentException">If the secret is not 32 bytes or the packet would be too long.</exception>
    internal static byte[] CreateErrorPacket(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> errorPayload)
    {
        if (HmacLength + errorPayload.Length > OnionConstants.MaxErrorPacketLength)
            throw new ArgumentException(
                $"The return packet would exceed {OnionConstants.MaxErrorPacketLength} bytes.", nameof(errorPayload));

        var packet = new byte[HmacLength + errorPayload.Length];
        errorPayload.CopyTo(packet.AsSpan(HmacLength));

        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        Span<byte> key = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            keyGenerator.DeriveKey(OnionConstants.Um, sharedSecret, key);
            keyGenerator.ComputeHmac(key, packet.AsSpan(HmacLength), ReadOnlySpan<byte>.Empty,
                                     packet.AsSpan(0, HmacLength));

            keyGenerator.DeriveKey(OnionConstants.Ammag, sharedSecret, key);
            chaCha20.Xor(key, packet, packet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return packet;
    }

    /// <inheritdoc />
    public byte[] WrapErrorPacket(Secret sharedSecret, ReadOnlySpan<byte> errorPacket)
    {
        // BOLT 4: truncate an over-long packet to its first 32768 bytes before transforming it
        if (errorPacket.Length > OnionConstants.MaxErrorPacketLength)
            errorPacket = errorPacket[..OnionConstants.MaxErrorPacketLength];

        var wrapped = errorPacket.ToArray();

        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        Span<byte> ammagKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            keyGenerator.DeriveKey(OnionConstants.Ammag, sharedSecret, ammagKey);
            chaCha20.Xor(ammagKey, wrapped, wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ammagKey);
        }

        return wrapped;
    }

    /// <inheritdoc />
    public DecryptedFailure? DecryptErrorPacket(IReadOnlyList<Secret> hopSharedSecrets,
                                                ReadOnlySpan<byte> errorPacket)
    {
        ArgumentNullException.ThrowIfNull(hopSharedSecrets);
        if (hopSharedSecrets.Count == 0)
            throw new ArgumentException("The route must have at least one hop.", nameof(hopSharedSecrets));

        foreach (var sharedSecret in hopSharedSecrets)
            if (((ReadOnlySpan<byte>)sharedSecret).Length != CryptoConstants.SecretLen)
                throw new ArgumentException("Every hop shared secret must be 32 bytes.", nameof(hopSharedSecrets));

        // Too short to even hold an HMAC: nobody on the route can have authenticated it
        if (errorPacket.Length < HmacLength)
            return null;

        var packet = errorPacket.ToArray();
        byte[]? erringPayload = null;
        var erringHopIndex = -1;

        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        Span<byte> ammagKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        Span<byte> umKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        Span<byte> hmac = stackalloc byte[HmacLength];
        try
        {
            // BOLT 4: keep decrypting until the loop ran 27 times, with constant keys past the end of the route, so
            // the erring node cannot learn its position from the origin's timing
            var iterations = Math.Max(OnionConstants.ErrorDecryptionIterations, hopSharedSecrets.Count);
            for (var i = 0; i < iterations; i++)
            {
                var sharedSecret = i < hopSharedSecrets.Count
                                       ? (ReadOnlySpan<byte>)hopSharedSecrets[i]
                                       : s_dummySharedSecret;

                keyGenerator.DeriveKey(OnionConstants.Ammag, sharedSecret, ammagKey);
                chaCha20.Xor(ammagKey, packet, packet);

                keyGenerator.DeriveKey(OnionConstants.Um, sharedSecret, umKey);
                keyGenerator.ComputeHmac(umKey, packet.AsSpan(HmacLength), ReadOnlySpan<byte>.Empty, hmac);

                var matches = CryptographicOperations.FixedTimeEquals(hmac, packet.AsSpan(0, HmacLength));
                if (matches & (erringHopIndex < 0) & (i < hopSharedSecrets.Count))
                {
                    erringHopIndex = i;
                    erringPayload = packet[HmacLength..];
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ammagKey);
            CryptographicOperations.ZeroMemory(umKey);
        }

        if (erringPayload is null)
            return null;

        if (!_failureMessageSerializer.TryReadErrorPayload(erringPayload, out var rawMessage))
            return new DecryptedFailure(erringHopIndex, ReadOnlyMemory<byte>.Empty, null);

        _failureMessageSerializer.TryDeserialize(rawMessage, out var message);
        return new DecryptedFailure(erringHopIndex, rawMessage, message);
    }
}