using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Tlv;

using Domain.Protocol.Models;
using Domain.Protocol.Tlv;
using Domain.Protocol.ValueObjects;
using Interfaces;

public class TlvStreamSerializer : ITlvStreamSerializer
{
    private readonly ITlvSerializer _tlvSerializer;
    private readonly ITlvConverterFactory _tlvConverterFactory;

    public TlvStreamSerializer(ITlvConverterFactory tlvConverterFactory, ITlvSerializer tlvSerializer)
    {
        _tlvConverterFactory = tlvConverterFactory;
        _tlvSerializer = tlvSerializer;
    }

    /// <summary>
    /// Serializes every TLV in <paramref name="tlvStream"/> in ascending type order.
    /// </summary>
    /// <remarks>
    /// Typed TLVs are converted through the converter registered for their exact runtime type. A raw
    /// <see cref="BaseTlv"/> (runtime type exactly <see cref="BaseTlv"/>) is written as-is.
    /// </remarks>
    /// <exception cref="SerializationException">Thrown when no converter is registered for a typed TLV.</exception>
    public async Task SerializeAsync(TlvStream? tlvStream, Stream stream)
    {
        if (tlvStream is null)
            return;

        foreach (var tlv in tlvStream.GetTlvs())
        {
            var baseTlv = ConvertToBase(tlv);
            await _tlvSerializer.SerializeAsync(baseTlv, stream);
        }
    }

    /// <summary>
    /// Deserializes a TLV stream until the end of <paramref name="stream"/>.
    /// </summary>
    /// <remarks>
    /// Enforces strictly increasing types and length bounds. Unknown even types are not rejected here because the
    /// known-type set depends on the message; use <see cref="DeserializeStrictAsync"/> when it is known.
    /// </remarks>
    /// <returns>The TLV stream, or <c>null</c> when the stream is empty.</returns>
    /// <exception cref="SerializationException">Thrown when the stream is invalid.</exception>
    public async Task<TlvStream?> DeserializeAsync(Stream stream)
    {
        if (stream.Position == stream.Length)
            return null;

        var tlvStream = await ReadTlvStreamAsync(stream, null);
        return tlvStream.Any() ? tlvStream : null;
    }

    /// <inheritdoc />
    public Task<TlvStream> DeserializeStrictAsync(Stream stream, IReadOnlySet<BigSize> knownTypes)
    {
        ArgumentNullException.ThrowIfNull(knownTypes);
        return ReadTlvStreamAsync(stream, knownTypes);
    }

    private BaseTlv ConvertToBase(BaseTlv tlv)
    {
        var runtimeType = tlv.GetType();
        if (runtimeType == typeof(BaseTlv))
            return tlv;

        var converter = _tlvConverterFactory.GetConverter(runtimeType)
                     ?? throw new SerializationException($"No converter found for tlv type {runtimeType.Name}");

        return converter.ConvertToBase(tlv);
    }

    private async Task<TlvStream> ReadTlvStreamAsync(Stream stream, IReadOnlySet<BigSize>? knownTypes)
    {
        try
        {
            var tlvStream = new TlvStream();
            BigSize? previousType = null;

            while (stream.Position != stream.Length)
            {
                var tlv = await _tlvSerializer.DeserializeAsync(stream);
                if (tlv is null)
                    break;

                // BOLT 1: types MUST be strictly increasing (this also rejects duplicates).
                if (previousType is { } previous && tlv.Type.Value <= previous.Value)
                    throw new SerializationException(
                        $"TLV type {tlv.Type.Value} is not greater than the previous type {previous.Value}.");

                // BOLT 1: an unknown even type MUST fail the stream.
                if (knownTypes is not null && tlv.Type.Value % 2 == 0 && !knownTypes.Contains(tlv.Type))
                    throw new SerializationException($"Unknown even TLV type {tlv.Type.Value}.");

                previousType = tlv.Type;
                tlvStream.Add(tlv);
            }

            return tlvStream;
        }
        catch (Exception e)
        {
            throw new SerializationException("Error deserializing TLVStream", e);
        }
    }
}