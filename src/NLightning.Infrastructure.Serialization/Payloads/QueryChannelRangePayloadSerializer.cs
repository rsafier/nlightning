using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;

/// <summary>
/// Serializer for <see cref="QueryChannelRangePayload"/>: <c>chain_hash</c>, <c>u32 first_blocknum</c>,
/// <c>u32 number_of_blocks</c> (BOLT 7).
/// </summary>
public class QueryChannelRangePayloadSerializer : IPayloadSerializer<QueryChannelRangePayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public QueryChannelRangePayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not QueryChannelRangePayload queryPayload)
            throw new SerializationException($"Payload is not of type {nameof(QueryChannelRangePayload)}");

        var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                               ?? throw new SerializationException(
                                      $"No serializer found for value object type {nameof(ChainHash)}");
        await chainHashSerializer.SerializeAsync(queryPayload.ChainHash, stream);

        await GossipQueryFieldSerializer.WriteU32Async(queryPayload.FirstBlocknum, stream);
        await GossipQueryFieldSerializer.WriteU32Async(queryPayload.NumberOfBlocks, stream);
    }

    public async Task<QueryChannelRangePayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                                   ?? throw new SerializationException(
                                          $"No serializer found for value object type {nameof(ChainHash)}");
            var chainHash = await chainHashSerializer.DeserializeAsync(stream);

            var firstBlocknum = await GossipQueryFieldSerializer.ReadU32Async(stream);
            var numberOfBlocks = await GossipQueryFieldSerializer.ReadU32Async(stream);

            return new QueryChannelRangePayload(chainHash, firstBlocknum, numberOfBlocks);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(QueryChannelRangePayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}