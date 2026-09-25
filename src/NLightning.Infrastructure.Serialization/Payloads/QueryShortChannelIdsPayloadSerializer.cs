using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;

/// <summary>
/// Serializer for <see cref="QueryShortChannelIdsPayload"/>: <c>chain_hash</c>, <c>u16 len</c>,
/// <c>len*byte encoded_short_ids</c> (BOLT 7).
/// </summary>
public class QueryShortChannelIdsPayloadSerializer : IPayloadSerializer<QueryShortChannelIdsPayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public QueryShortChannelIdsPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not QueryShortChannelIdsPayload queryPayload)
            throw new SerializationException($"Payload is not of type {nameof(QueryShortChannelIdsPayload)}");

        var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                               ?? throw new SerializationException(
                                      $"No serializer found for value object type {nameof(ChainHash)}");
        await chainHashSerializer.SerializeAsync(queryPayload.ChainHash, stream);

        await GossipQueryFieldSerializer.WriteU16PrefixedBytesAsync(queryPayload.EncodedShortIds, stream);
    }

    public async Task<QueryShortChannelIdsPayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                                   ?? throw new SerializationException(
                                          $"No serializer found for value object type {nameof(ChainHash)}");
            var chainHash = await chainHashSerializer.DeserializeAsync(stream);

            var encodedShortIds = await GossipQueryFieldSerializer.ReadU16PrefixedBytesAsync(stream);

            return new QueryShortChannelIdsPayload(chainHash, encodedShortIds);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(QueryShortChannelIdsPayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}