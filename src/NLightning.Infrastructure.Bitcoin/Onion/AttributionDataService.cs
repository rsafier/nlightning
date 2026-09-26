using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
using Infrastructure.Crypto.Ciphers;

/// <summary>
/// BOLT 4 attributable failures and hold times: <c>attribution_data</c> for <c>update_fail_htlc</c> and
/// <c>update_fulfill_htlc</c>, and the <c>fulfillment_payload</c> of <c>update_fulfill_htlc</c>.
/// </summary>
/// <remarks>
/// <para>
/// Layout of the 210 truncated HMACs, for node <c>x</c> (0 = the node handling the data) at <c>y</c> hops from the
/// erring (or final) node: node <c>x</c>'s block starts at <c>sum(20 - k, k &lt; x)</c> and holds its HMACs for
/// <c>y = 19 - x</c> down to <c>0</c>, so <c>hmac_x_y</c> is at <c>start(x) + (19 - x - y)</c>. The downstream HMACs
/// covered by <c>hmac_0_y</c> are <c>hmac_j_(y-j)</c> for <c>j = 1..y</c>, at <c>start(j) + 19 - y</c>.
/// </para>
/// <para>
/// The return packet is left to <see cref="IFailureOnionService"/>. Its <c>ammag</c> XOR is its own inverse, so the
/// packet "before applying the pseudo-random byte stream" is recovered by applying it once more.
/// </para>
/// <para>Stateless and thread-safe: every call allocates its own key generator and ciphers.</para>
/// </remarks>
internal sealed class AttributionDataService : IAttributionDataService
{
    private const int MaxHops = OnionConstants.AttributionMaxHops;
    private const int HoldTimeLength = OnionConstants.AttributionHoldTimeLength;
    private const int HmacLength = OnionConstants.AttributionHmacLength;
    private const int HoldTimesLength = OnionConstants.AttributionHoldTimesLength;
    private const int DataLength = OnionConstants.AttributionDataLength;
    private const int TagLength = CryptoConstants.Chacha20Poly1305TagLen;

    private readonly IFailureOnionService _failureOnionService;

    public AttributionDataService(IFailureOnionService failureOnionService)
    {
        _failureOnionService = failureOnionService ?? throw new ArgumentNullException(nameof(failureOnionService));
    }

    #region Failures

    /// <inheritdoc />
    public AttributedErrorPacket CreateErrorPacket(Secret sharedSecret, FailureMessage message, uint holdTime,
                                                   int minFailurePadLength = OnionConstants.MinFailurePadLength)
    {
        var reason = _failureOnionService.CreateErrorPacket(sharedSecret, message, minFailurePadLength);
        return AttributeCreatedPacket(sharedSecret, reason, holdTime);
    }

    /// <inheritdoc />
    public AttributedErrorPacket CreateErrorPacketFromMalformed(Secret incomingSharedSecret, FailureCode failureCode,
                                                                ReadOnlySpan<byte> sha256OfOnion, uint holdTime,
                                                                int minFailurePadLength =
                                                                    OnionConstants.MinFailurePadLength)
    {
        var reason = _failureOnionService.CreateErrorPacketFromMalformed(incomingSharedSecret, failureCode,
                                                                         sha256OfOnion, minFailurePadLength);
        return AttributeCreatedPacket(incomingSharedSecret, reason, holdTime);
    }

    /// <inheritdoc />
    public AttributedErrorPacket WrapErrorPacket(Secret sharedSecret, ReadOnlySpan<byte> errorPacket,
                                                 ReadOnlySpan<byte> downstreamAttributionData, uint holdTime)
    {
        // BOLT 4: truncate before transforming the return packet or updating attribution_data
        if (errorPacket.Length > OnionConstants.MaxErrorPacketLength)
            errorPacket = errorPacket[..OnionConstants.MaxErrorPacketLength];

        var attributionData = UpdateAttributionData(sharedSecret, errorPacket, downstreamAttributionData, holdTime);
        var reason = _failureOnionService.WrapErrorPacket(sharedSecret, errorPacket);
        return new AttributedErrorPacket(reason, attributionData);
    }

    /// <inheritdoc />
    public AttributedFailure DecryptErrorPacket(IReadOnlyList<Secret> hopSharedSecrets, ReadOnlySpan<byte> errorPacket,
                                                ReadOnlySpan<byte> attributionData)
    {
        ArgumentNullException.ThrowIfNull(hopSharedSecrets);

        var failure = _failureOnionService.DecryptErrorPacket(hopSharedSecrets, errorPacket);
        if (attributionData.Length != DataLength || hopSharedSecrets.Count > MaxHops)
            return new AttributedFailure(failure, AttributionVerification.Absent);

        if (errorPacket.Length > OnionConstants.MaxErrorPacketLength)
            errorPacket = errorPacket[..OnionConstants.MaxErrorPacketLength];

        // Only the hops up to the erring one added attribution data; past it nothing can verify
        var hopsToVerify = failure is null ? hopSharedSecrets.Count : failure.ErringHopIndex + 1;
        var packet = errorPacket.ToArray();
        var verification = VerifyHops(hopSharedSecrets, hopsToVerify, attributionData, (i, keyGenerator, chaCha20) =>
        {
            // The packet as hop i received it = the origin's packet with the ammag layers of hops 0..i removed
            DeriveAndXor(keyGenerator, chaCha20, OnionConstants.Ammag, hopSharedSecrets[i], packet);
            return packet;
        });

        return new AttributedFailure(failure, verification);
    }

    #endregion

    #region Fulfillments

    /// <inheritdoc />
    public AttributedFulfillment CreateFulfillment(Secret sharedSecret, uint holdTime,
                                                   IReadOnlyList<BaseTlv>? fulfillmentRecords = null)
    {
        byte[]? fulfillmentPayload = null;
        if (fulfillmentRecords is not null)
            fulfillmentPayload = EncryptFulfillmentPayload(sharedSecret, fulfillmentRecords);

        // BOLT 4: the final node's HMACs do not cover the fulfillment_payload
        var attributionData = UpdateAttributionData(sharedSecret, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty,
                                                    holdTime);
        return new AttributedFulfillment(attributionData, fulfillmentPayload);
    }

    /// <inheritdoc />
    public AttributedFulfillment WrapFulfillment(Secret sharedSecret, ReadOnlySpan<byte> downstreamAttributionData,
                                                 ReadOnlySpan<byte> downstreamFulfillmentPayload, uint holdTime)
    {
        EnsureFulfillmentPayloadLength(downstreamFulfillmentPayload, nameof(downstreamFulfillmentPayload));

        var attributionData = UpdateAttributionData(sharedSecret, downstreamFulfillmentPayload,
                                                    downstreamAttributionData, holdTime);
        var fulfillmentPayload = downstreamFulfillmentPayload.IsEmpty
                                     ? null
                                     : WrapFulfillmentPayload(sharedSecret, downstreamFulfillmentPayload);
        return new AttributedFulfillment(attributionData, fulfillmentPayload);
    }

    /// <inheritdoc />
    public byte[] WrapFulfillmentPayload(Secret sharedSecret, ReadOnlySpan<byte> fulfillmentPayload)
    {
        EnsureFulfillmentPayloadLength(fulfillmentPayload, nameof(fulfillmentPayload));

        var wrapped = fulfillmentPayload.ToArray();
        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        DeriveAndXor(keyGenerator, chaCha20, OnionConstants.Ammag, sharedSecret, wrapped);
        return wrapped;
    }

    /// <inheritdoc />
    public VerifiedFulfillment VerifyFulfillment(IReadOnlyList<Secret> hopSharedSecrets,
                                                 ReadOnlySpan<byte> attributionData,
                                                 ReadOnlySpan<byte> fulfillmentPayload)
    {
        ArgumentNullException.ThrowIfNull(hopSharedSecrets);
        if (hopSharedSecrets.Count == 0)
            throw new ArgumentException("The route must have at least one hop.", nameof(hopSharedSecrets));

        var hopCount = hopSharedSecrets.Count;
        var finalHop = hopCount - 1;
        var payload = fulfillmentPayload.IsEmpty ? null : fulfillmentPayload.ToArray();

        var verification = AttributionVerification.Absent;
        if (attributionData.Length == DataLength && hopCount <= MaxHops)
        {
            verification = VerifyHops(hopSharedSecrets, hopCount, attributionData, (i, keyGenerator, chaCha20) =>
            {
                // The final node does not obfuscate the payload and its HMACs do not cover it. Every intermediate
                // hop covered the payload as it received it from downstream: the origin's with the ammag layers of
                // hops 0..i removed.
                if (payload is null || i == finalHop)
                    return ReadOnlySpan<byte>.Empty;

                DeriveAndXor(keyGenerator, chaCha20, OnionConstants.Ammag, hopSharedSecrets[i], payload);
                return payload;
            });

            // Hops the verification stopped before still have to be peeled off the payload
            if (payload is not null)
                PeelPayload(hopSharedSecrets, payload,
                            Math.Min(verification.VerifiedHopCount + (verification.InvalidHopIndex is null ? 0 : 1),
                                     finalHop), finalHop);
        }
        else if (payload is not null)
        {
            PeelPayload(hopSharedSecrets, payload, 0, finalHop);
        }

        if (payload is null)
            return new VerifiedFulfillment(verification, FulfillmentPayloadStatus.None, []);

        return TryDecryptFulfillmentPayload(hopSharedSecrets[finalHop], payload, out var records)
                   ? new VerifiedFulfillment(verification, FulfillmentPayloadStatus.Valid, records)
                   : new VerifiedFulfillment(verification, FulfillmentPayloadStatus.Invalid, []);
    }

    #endregion

    #region attribution_data

    /// <summary>
    /// Transforms (shift and prune) the downstream attribution data, or starts from an all-zero block when there is
    /// none, then puts this node's hold time and HMACs in front and obfuscates it with <c>ammagext</c>.
    /// </summary>
    /// <param name="sharedSecret">This node's shared secret.</param>
    /// <param name="coveredMessage">
    /// What the HMACs cover besides the hold times and downstream HMACs: the return packet before this node's
    /// <c>ammag</c> stream, the downstream <c>fulfillment_payload</c>, or nothing.
    /// </param>
    /// <param name="downstreamAttributionData">The received attribution data; anything but 920 bytes counts as none.</param>
    /// <param name="holdTime">This node's hold time.</param>
    internal static byte[] UpdateAttributionData(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> coveredMessage,
                                                 ReadOnlySpan<byte> downstreamAttributionData, uint holdTime)
    {
        EnsureSecret(sharedSecret);

        var data = new byte[DataLength];
        if (downstreamAttributionData.Length == DataLength)
            ShiftRight(downstreamAttributionData, data);

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, HoldTimeLength), holdTime);

        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        Span<byte> umKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            keyGenerator.DeriveKey(OnionConstants.Um, sharedSecret, umKey);
            var hmacInput = new byte[HoldTimesLength + (MaxHops - 1) * HmacLength];
            Span<byte> hmac = stackalloc byte[CryptoConstants.Sha256HashLen];
            for (var position = 0; position < MaxHops; position++)
            {
                ComputeHmac(keyGenerator, umKey, coveredMessage, data, position, hmacInput, hmac);
                hmac[..HmacLength].CopyTo(data.AsSpan(HoldTimesLength + OwnHmacIndex(position) * HmacLength, HmacLength));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(umKey);
        }

        DeriveAndXor(keyGenerator, chaCha20, OnionConstants.AmmagExt, sharedSecret, data);
        return data;
    }

    /// <summary>
    /// Verifies the HMACs of hops 0..<paramref name="hopsToVerify"/>-1, each at its position in the route
    /// (<c>route length - 1 - i</c>), stopping at the first one that does not match.
    /// </summary>
    /// <param name="coveredMessageOfHop">
    /// Returns the message hop <c>i</c> covered; called once per hop, in order, before that hop is verified.
    /// </param>
    private static AttributionVerification VerifyHops(IReadOnlyList<Secret> hopSharedSecrets, int hopsToVerify,
                                                      ReadOnlySpan<byte> attributionData,
                                                      CoveredMessageOfHop coveredMessageOfHop)
    {
        foreach (var sharedSecret in hopSharedSecrets)
            EnsureSecret(sharedSecret);

        var routeLength = hopSharedSecrets.Count;
        var data = attributionData.ToArray();
        var holdTimes = new List<uint>(hopsToVerify);
        int? invalidHopIndex = null;

        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        Span<byte> umKey = stackalloc byte[CryptoConstants.Sha256HashLen];
        Span<byte> hmac = stackalloc byte[CryptoConstants.Sha256HashLen];
        var hmacInput = new byte[HoldTimesLength + (MaxHops - 1) * HmacLength];
        try
        {
            for (var i = 0; i < hopsToVerify; i++)
            {
                var sharedSecret = (ReadOnlySpan<byte>)hopSharedSecrets[i];
                var coveredMessage = coveredMessageOfHop(i, keyGenerator, chaCha20);

                // Remove hop i's ammagext layer: data is now what hop i produced before obfuscating it
                DeriveAndXor(keyGenerator, chaCha20, OnionConstants.AmmagExt, sharedSecret, data);

                var position = routeLength - 1 - i;
                keyGenerator.DeriveKey(OnionConstants.Um, sharedSecret, umKey);
                ComputeHmac(keyGenerator, umKey, coveredMessage, data, position, hmacInput, hmac);
                var received = data.AsSpan(HoldTimesLength + OwnHmacIndex(position) * HmacLength, HmacLength);
                if (!CryptographicOperations.FixedTimeEquals(hmac[..HmacLength], received))
                {
                    invalidHopIndex = i;
                    break;
                }

                holdTimes.Add(BinaryPrimitives.ReadUInt32BigEndian(data));

                // Undo hop i's shift: data is now what hop i received from downstream (pruned bytes left zero)
                var downstream = new byte[DataLength];
                ShiftLeft(data, downstream);
                data = downstream;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(umKey);
        }

        return new AttributionVerification(true, holdTimes, invalidHopIndex);
    }

    /// <summary>
    /// <c>hmac_0_position</c> = the first 4 bytes of HMAC(um, message || hold_times[0..position] ||
    /// hmac_1_(position-1) || ... || hmac_position_0).
    /// </summary>
    private static void ComputeHmac(SphinxKeyGenerator keyGenerator, ReadOnlySpan<byte> umKey,
                                    ReadOnlySpan<byte> coveredMessage, ReadOnlySpan<byte> data, int position,
                                    Span<byte> buffer, Span<byte> output)
    {
        var length = (position + 1) * HoldTimeLength;
        data[..length].CopyTo(buffer);

        var hmacs = Hmacs(data);
        for (var j = 1; j <= position; j++)
        {
            var index = BlockStart(j) + MaxHops - 1 - position;
            hmacs.Slice(index * HmacLength, HmacLength).CopyTo(buffer[length..]);
            length += HmacLength;
        }

        keyGenerator.ComputeHmac(umKey, coveredMessage, buffer[..length], output);
    }

    /// <summary>
    /// The upstream transformation: hold times move one slot right (the last one drops off), node <c>x</c>'s HMAC
    /// block becomes node <c>x + 1</c>'s without its left-most entry (and node 19's drops off), leaving hold time 0
    /// and block 0 zero.
    /// </summary>
    private static void ShiftRight(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source[..(HoldTimesLength - HoldTimeLength)].CopyTo(destination[HoldTimeLength..HoldTimesLength]);

        var sourceHmacs = source[HoldTimesLength..];
        var destinationHmacs = destination[HoldTimesLength..];
        for (var x = 0; x < MaxHops - 1; x++)
        {
            var count = MaxHops - x - 1;
            sourceHmacs.Slice((BlockStart(x) + 1) * HmacLength, count * HmacLength)
                       .CopyTo(destinationHmacs[(BlockStart(x + 1) * HmacLength)..]);
        }
    }

    /// <summary>
    /// The inverse of <see cref="ShiftRight"/> at the origin; the pruned bytes come back as zeros. They are never
    /// covered by the HMAC of a hop within 20 hops of the end of the route.
    /// </summary>
    private static void ShiftLeft(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source[HoldTimeLength..HoldTimesLength].CopyTo(destination);

        var sourceHmacs = source[HoldTimesLength..];
        var destinationHmacs = destination[HoldTimesLength..];
        for (var x = 0; x < MaxHops - 1; x++)
        {
            var count = MaxHops - x - 1;
            sourceHmacs.Slice(BlockStart(x + 1) * HmacLength, count * HmacLength)
                       .CopyTo(destinationHmacs[((BlockStart(x) + 1) * HmacLength)..]);
        }
    }

    /// <summary>
    /// The index of node <paramref name="x"/>'s first HMAC: 20 + 19 + ... + (20 - x + 1).
    /// </summary>
    private static int BlockStart(int x) => x * MaxHops - x * (x - 1) / 2;

    /// <summary>
    /// The index of <c>hmac_0_position</c> (this node's HMAC for the given position).
    /// </summary>
    private static int OwnHmacIndex(int position) => MaxHops - 1 - position;

    private static ReadOnlySpan<byte> Hmacs(ReadOnlySpan<byte> data) => data[HoldTimesLength..];

    private AttributedErrorPacket AttributeCreatedPacket(Secret sharedSecret, byte[] reason, uint holdTime)
    {
        // The HMACs cover the packet before the erring node's own ammag stream; the XOR is its own inverse
        var rawPacket = _failureOnionService.WrapErrorPacket(sharedSecret, reason);
        var attributionData = UpdateAttributionData(sharedSecret, rawPacket, ReadOnlySpan<byte>.Empty, holdTime);
        return new AttributedErrorPacket(reason, attributionData);
    }

    private delegate ReadOnlySpan<byte> CoveredMessageOfHop(int hopIndex, SphinxKeyGenerator keyGenerator,
                                                            ChaCha20Stream chaCha20);

    #endregion

    #region fulfillment_payload

    /// <summary>
    /// Serializes <c>fulfillment_payload_tlvs</c> (records + <c>padding</c>) and encrypts it with the final node's
    /// <c>fulfillment</c> key.
    /// </summary>
    internal static byte[] EncryptFulfillmentPayload(ReadOnlySpan<byte> sharedSecret,
                                                     IReadOnlyList<BaseTlv> fulfillmentRecords)
    {
        EnsureSecret(sharedSecret);
        var plaintext = SerializeFulfillmentPayloadTlvs(fulfillmentRecords);

        var payload = new byte[plaintext.Length + TagLength];
        using var keyGenerator = new SphinxKeyGenerator();
        using var aead = new ChaCha20Poly1305();
        Span<byte> key = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            keyGenerator.DeriveKey(OnionConstants.Fulfillment, sharedSecret, key);
            aead.Encrypt(key, 0, ReadOnlySpan<byte>.Empty, plaintext, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return payload;
    }

    /// <summary>
    /// The records sorted by type plus a <c>padding</c> record, so that the stream is a multiple of 256 bytes (256
    /// when possible). BOLT 4 counts the padding record's own type and length bytes.
    /// </summary>
    internal static byte[] SerializeFulfillmentPayloadTlvs(IReadOnlyList<BaseTlv> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var types = new HashSet<ulong>();
        var contentLength = 0;
        foreach (var record in records)
        {
            ArgumentNullException.ThrowIfNull(record);
            ulong type = record.Type;
            if (type == FulfillmentPayloadTlvTypes.Padding)
                throw new ArgumentException("The padding record is added by the service.", nameof(records));
            if (!types.Add(type))
                throw new ArgumentException($"Duplicate fulfillment_payload_tlvs type {type}.", nameof(records));

            contentLength += SphinxBigSize.GetEncodedLength(type)
                           + SphinxBigSize.GetEncodedLength((ulong)record.Value.Length) + record.Value.Length;
        }

        var paddingLength = -1;
        var total = OnionConstants.FulfillmentPayloadPaddingBlock;
        while (paddingLength < 0)
        {
            if (total + TagLength > OnionConstants.MaxFulfillmentPayloadLength)
                throw new ArgumentException("The fulfillment_payload would exceed 32768 bytes.", nameof(records));

            paddingLength = FindPaddingLength(total - contentLength);
            if (paddingLength < 0)
                total += OnionConstants.FulfillmentPayloadPaddingBlock;
        }

        var padding = new BaseTlv(FulfillmentPayloadTlvTypes.Padding, new byte[paddingLength]);
        var buffer = new byte[total];
        var offset = 0;
        foreach (var record in records.Append(padding).OrderBy(r => (ulong)r.Type))
        {
            offset += SphinxBigSize.Write(record.Type, buffer.AsSpan(offset));
            offset += SphinxBigSize.Write((ulong)record.Value.Length, buffer.AsSpan(offset));
            record.Value.CopyTo(buffer, offset);
            offset += record.Value.Length;
        }

        return buffer;
    }

    /// <summary>
    /// The value length of a <c>padding</c> record that takes exactly <paramref name="recordLength"/> bytes, or -1.
    /// </summary>
    private static int FindPaddingLength(int recordLength)
    {
        // type (1 byte) + BigSize length (1, 3 or 5 bytes) + value
        for (var lengthOfLength = 1; lengthOfLength <= 5; lengthOfLength += 2)
        {
            var valueLength = recordLength - 1 - lengthOfLength;
            if (valueLength >= 0 && SphinxBigSize.GetEncodedLength((ulong)valueLength) == lengthOfLength)
                return valueLength;
        }

        return -1;
    }

    private static bool TryDecryptFulfillmentPayload(ReadOnlySpan<byte> finalHopSharedSecret, byte[] payload,
                                                     out IReadOnlyList<BaseTlv> records)
    {
        records = [];
        if (payload.Length < TagLength)
            return false;

        var plaintext = new byte[payload.Length - TagLength];
        using var keyGenerator = new SphinxKeyGenerator();
        using var aead = new ChaCha20Poly1305();
        Span<byte> key = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            keyGenerator.DeriveKey(OnionConstants.Fulfillment, finalHopSharedSecret, key);
            aead.Decrypt(key, 0, ReadOnlySpan<byte>.Empty, payload, plaintext);
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return TryParseFulfillmentPayloadTlvs(plaintext, out records);
    }

    /// <summary>
    /// Reads <c>fulfillment_payload_tlvs</c> strictly (canonical BigSizes, strictly increasing types, lengths within
    /// the stream, no unknown even type) and returns every record but <c>padding</c>.
    /// </summary>
    internal static bool TryParseFulfillmentPayloadTlvs(ReadOnlySpan<byte> stream, out IReadOnlyList<BaseTlv> records)
    {
        var result = new List<BaseTlv>();
        records = [];
        ulong? previousType = null;
        while (!stream.IsEmpty)
        {
            if (!SphinxBigSize.TryRead(stream, out var type, out var typeLength))
                return false;
            stream = stream[typeLength..];

            if (!SphinxBigSize.TryRead(stream, out var length, out var lengthLength))
                return false;
            stream = stream[lengthLength..];

            if (length > (ulong)stream.Length || (previousType.HasValue && type <= previousType.Value))
                return false;

            // BOLT 1: an unknown even type fails the stream; padding is the only known type
            if (type != FulfillmentPayloadTlvTypes.Padding && type % 2 == 0)
                return false;

            if (type != FulfillmentPayloadTlvTypes.Padding)
                result.Add(new BaseTlv(type, stream[..(int)length].ToArray()));

            stream = stream[(int)length..];
            previousType = type;
        }

        records = result;
        return true;
    }

    private static void PeelPayload(IReadOnlyList<Secret> hopSharedSecrets, byte[] payload, int fromHop, int toHop)
    {
        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();
        for (var i = fromHop; i < toHop; i++)
            DeriveAndXor(keyGenerator, chaCha20, OnionConstants.Ammag, hopSharedSecrets[i], payload);
    }

    private static void EnsureFulfillmentPayloadLength(ReadOnlySpan<byte> payload, string paramName)
    {
        if (payload.Length > OnionConstants.MaxFulfillmentPayloadLength)
            throw new ArgumentException(
                $"A fulfillment_payload must not exceed {OnionConstants.MaxFulfillmentPayloadLength} bytes.",
                paramName);
    }

    #endregion

    private static void DeriveAndXor(SphinxKeyGenerator keyGenerator, ChaCha20Stream chaCha20,
                                     ReadOnlySpan<byte> label, ReadOnlySpan<byte> sharedSecret, Span<byte> data)
    {
        Span<byte> key = stackalloc byte[CryptoConstants.Sha256HashLen];
        try
        {
            keyGenerator.DeriveKey(label, sharedSecret, key);
            chaCha20.Xor(key, data, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void EnsureSecret(ReadOnlySpan<byte> sharedSecret)
    {
        if (sharedSecret.Length != CryptoConstants.SecretLen)
            throw new ArgumentException("Every shared secret must be 32 bytes.", nameof(sharedSecret));
    }
}