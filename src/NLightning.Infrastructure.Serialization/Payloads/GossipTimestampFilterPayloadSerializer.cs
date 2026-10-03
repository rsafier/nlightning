using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;

/// <summary>
/// Serializer for <see cref="GossipTimestampFilterPayload"/>: <c>chain_hash</c>, <c>u32 first_timestamp</c>,
/// <c>u32 timestamp_range</c> (BOLT 7).
/// </summary>
public class GossipTimestampFilterPayloadSerializer : IPayloadSerializer<GossipTimestampFilterPayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public GossipTimestampFilterPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not GossipTimestampFilterPayload filterPayload)
            throw new SerializationException($"Payload is not of type {nameof(GossipTimestampFilterPayload)}");

        var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                               ?? throw new SerializationException(
                                      $"No serializer found for value object type {nameof(ChainHash)}");
        await chainHashSerializer.SerializeAsync(filterPayload.ChainHash, stream);

        await GossipQueryFieldSerializer.WriteU32Async(filterPayload.FirstTimestamp, stream);
        await GossipQueryFieldSerializer.WriteU32Async(filterPayload.TimestampRange, stream);
    }

    public async Task<GossipTimestampFilterPayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                                   ?? throw new SerializationException(
                                          $"No serializer found for value object type {nameof(ChainHash)}");
            var chainHash = await chainHashSerializer.DeserializeAsync(stream);

            var firstTimestamp = await GossipQueryFieldSerializer.ReadU32Async(stream);
            var timestampRange = await GossipQueryFieldSerializer.ReadU32Async(stream);

            return new GossipTimestampFilterPayload(chainHash, firstTimestamp, timestampRange);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(GossipTimestampFilterPayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}