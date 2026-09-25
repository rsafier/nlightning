using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Onion;

using Domain.Protocol.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Onion.Tlv;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Interfaces;

/// <inheritdoc />
/// <remarks>
/// Records are read one at a time (rather than through <see cref="ITlvStreamSerializer.DeserializeStrictAsync"/>)
/// so the failing record's type and byte offset can be reported, as <c>invalid_onion_payload</c> requires. The rules
/// are the same as the strict stream reader's, plus converter-level checks of every known type.
/// </remarks>
public class HopPayloadSerializer : IHopPayloadSerializer
{
    /// <summary>
    /// BOLT 4: a payload length of 0 (legacy) or 1 (reserved) is invalid.
    /// </summary>
    public const int MinPayloadLength = 2;

    private readonly ITlvSerializer _tlvSerializer;
    private readonly ITlvStreamSerializer _tlvStreamSerializer;
    private readonly IValueObjectTypeSerializer<BigSize> _bigSizeSerializer;
    private readonly Dictionary<BigSize, ITlvConverter> _converters;

    public HopPayloadSerializer(ITlvSerializer tlvSerializer, ITlvStreamSerializer tlvStreamSerializer,
                                ITlvConverterFactory tlvConverterFactory,
                                IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        ArgumentNullException.ThrowIfNull(tlvConverterFactory);
        ArgumentNullException.ThrowIfNull(valueObjectSerializerFactory);

        _tlvSerializer = tlvSerializer ?? throw new ArgumentNullException(nameof(tlvSerializer));
        _tlvStreamSerializer = tlvStreamSerializer ?? throw new ArgumentNullException(nameof(tlvStreamSerializer));
        _bigSizeSerializer = valueObjectSerializerFactory.GetSerializer<BigSize>()
                          ?? throw new ArgumentException("No BigSize serializer registered.",
                                                         nameof(valueObjectSerializerFactory));

        _converters = new Dictionary<BigSize, ITlvConverter>
        {
            [OnionPayloadTlvTypes.AmtToForward] = GetConverter<AmtToForwardTlv>(tlvConverterFactory),
            [OnionPayloadTlvTypes.OutgoingCltvValue] = GetConverter<OutgoingCltvValueTlv>(tlvConverterFactory),
            [OnionPayloadTlvTypes.ShortChannelId] = GetConverter<OnionShortChannelIdTlv>(tlvConverterFactory),
            [OnionPayloadTlvTypes.PaymentData] = GetConverter<PaymentDataTlv>(tlvConverterFactory),
            [OnionPayloadTlvTypes.EncryptedRecipientData] =
                GetConverter<EncryptedRecipientDataTlv>(tlvConverterFactory),
            [OnionPayloadTlvTypes.CurrentPathKey] = GetConverter<CurrentPathKeyTlv>(tlvConverterFactory),
            [OnionPayloadTlvTypes.PaymentMetadata] = GetConverter<PaymentMetadataTlv>(tlvConverterFactory),
            [OnionPayloadTlvTypes.TotalAmountMsat] = GetConverter<TotalAmountMsatTlv>(tlvConverterFactory)
        };
    }

    /// <inheritdoc />
    public async Task<HopPayload> DeserializeAsync(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < MinPayloadLength)
            throw InvalidOnionPayloadFailureFactory.Create(
                0, 0, $"Hop payload must be at least {MinPayloadLength} bytes, got {payload.Length}.");

        await using var stream = MemoryMarshal.TryGetArray(payload, out var segment)
                                     ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
                                     : new MemoryStream(payload.ToArray(), false);

        return await ReadPayloadAsync(stream, 0);
    }

    /// <inheritdoc />
    /// <remarks><paramref name="stream"/> must be seekable (its length bounds the payload length).</remarks>
    public async Task<HopPayload> DeserializeWithLengthPrefixAsync(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var start = stream.Position;
        BigSize length;
        try
        {
            length = await _bigSizeSerializer.DeserializeAsync(stream);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            throw InvalidOnionPayloadFailureFactory.Create(0, 0, "Malformed hop payload length.", e);
        }

        var prefixLength = (int)(stream.Position - start);

        if (length.Value < MinPayloadLength)
            throw InvalidOnionPayloadFailureFactory.Create(
                0, 0, $"Hop payload length {length.Value} is below the minimum of {MinPayloadLength}.");

        var remaining = (ulong)(stream.Length - stream.Position);
        if (length.Value > remaining)
            throw InvalidOnionPayloadFailureFactory.Create(
                0, 0, $"Hop payload length {length.Value} exceeds the {remaining} bytes remaining.");

        var buffer = new byte[(int)length.Value];
        await stream.ReadExactlyAsync(buffer);

        await using var payloadStream = new MemoryStream(buffer, false);
        return await ReadPayloadAsync(payloadStream, prefixLength);
    }

    /// <inheritdoc />
    public async Task SerializeAsync(HopPayload payload, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var bytes = await EncodeAsync(payload);
        await stream.WriteAsync(bytes);
    }

    /// <inheritdoc />
    public async Task SerializeWithLengthPrefixAsync(HopPayload payload, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var bytes = await EncodeAsync(payload);
        await _bigSizeSerializer.SerializeAsync(new BigSize((ulong)bytes.Length), stream);
        await stream.WriteAsync(bytes);
    }

    private async Task<byte[]> EncodeAsync(HopPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var buffer = new MemoryStream();
        await _tlvStreamSerializer.SerializeAsync(payload.ToTlvStream(), buffer);

        if (buffer.Length < MinPayloadLength)
            throw new ArgumentException($"A hop payload must encode to at least {MinPayloadLength} bytes.",
                                        nameof(payload));

        return buffer.ToArray();
    }

    private async Task<HopPayload> ReadPayloadAsync(Stream stream, int baseOffset)
    {
        var tlvStream = new TlvStream();
        var offsets = new Dictionary<BigSize, int>();
        BigSize? previousType = null;

        while (stream.Position < stream.Length)
        {
            var recordStart = stream.Position;
            var offset = baseOffset + (int)recordStart;

            BaseTlv? record;
            try
            {
                record = await _tlvSerializer.DeserializeAsync(stream);
            }
            catch (Exception e) when (e is ArgumentException or SerializationException or IOException)
            {
                var type = await TryReadTypeAsync(stream, recordStart);
                throw InvalidOnionPayloadFailureFactory.Create(type ?? 0, type is null ? 0 : offset,
                                                               "Malformed hop payload TLV record.", e);
            }

            if (record is null)
                break;

            // BOLT 1: types MUST be strictly increasing (this also rejects duplicates).
            if (previousType is { } previous && record.Type.Value <= previous.Value)
                throw InvalidOnionPayloadFailureFactory.Create(
                    record.Type, offset,
                    $"TLV type {record.Type.Value} is not greater than the previous type {previous.Value}.");

            tlvStream.Add(ConvertRecord(record, offset));
            offsets[record.Type] = offset;
            previousType = record.Type;
        }

        return new HopPayload(tlvStream, offsets);
    }

    private BaseTlv ConvertRecord(BaseTlv record, int offset)
    {
        if (!_converters.TryGetValue(record.Type, out var converter))
        {
            // BOLT 1: an unknown even type MUST fail; unknown odd types are kept verbatim.
            if (record.Type.Value % 2 == 0)
                throw InvalidOnionPayloadFailureFactory.Create(record.Type, offset,
                                                               $"Unknown even TLV type {record.Type.Value}.");

            return record;
        }

        try
        {
            return converter.ConvertFromBase(record);
        }
        catch (Exception e) when (e is InvalidCastException or ArgumentException)
        {
            throw InvalidOnionPayloadFailureFactory.Create(record.Type, offset,
                                                           $"Invalid value for TLV type {record.Type.Value}.", e);
        }
    }

    private async Task<BigSize?> TryReadTypeAsync(Stream stream, long recordStart)
    {
        stream.Position = recordStart;
        try
        {
            return await _bigSizeSerializer.DeserializeAsync(stream);
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            return null;
        }
    }

    private static ITlvConverter GetConverter<TTlv>(ITlvConverterFactory tlvConverterFactory) where TTlv : BaseTlv
    {
        return tlvConverterFactory.GetConverter(typeof(TTlv))
            ?? throw new ArgumentException($"No converter registered for {typeof(TTlv).Name}.",
                                           nameof(tlvConverterFactory));
    }
}