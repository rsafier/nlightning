using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;

/// <summary>
/// Serializer for <see cref="ReplyShortChannelIdsEndPayload"/>: <c>chain_hash</c>, <c>byte full_information</c>
/// (BOLT 7).
/// </summary>
public class ReplyShortChannelIdsEndPayloadSerializer : IPayloadSerializer<ReplyShortChannelIdsEndPayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public ReplyShortChannelIdsEndPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not ReplyShortChannelIdsEndPayload replyPayload)
            throw new SerializationException($"Payload is not of type {nameof(ReplyShortChannelIdsEndPayload)}");

        var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                               ?? throw new SerializationException(
                                      $"No serializer found for value object type {nameof(ChainHash)}");
        await chainHashSerializer.SerializeAsync(replyPayload.ChainHash, stream);

        await GossipQueryFieldSerializer.WriteBoolByteAsync(replyPayload.FullInformation, stream);
    }

    public async Task<ReplyShortChannelIdsEndPayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                                   ?? throw new SerializationException(
                                          $"No serializer found for value object type {nameof(ChainHash)}");
            var chainHash = await chainHashSerializer.DeserializeAsync(stream);

            var fullInformation = await GossipQueryFieldSerializer.ReadBoolByteAsync(stream);

            return new ReplyShortChannelIdsEndPayload(chainHash, fullInformation);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(ReplyShortChannelIdsEndPayload)}",
                                                    e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}