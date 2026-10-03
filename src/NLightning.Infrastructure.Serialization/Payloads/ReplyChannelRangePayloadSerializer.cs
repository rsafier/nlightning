using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Domain.Protocol.ValueObjects;
using Exceptions;

/// <summary>
/// Serializer for <see cref="ReplyChannelRangePayload"/>: <c>chain_hash</c>, <c>u32 first_blocknum</c>,
/// <c>u32 number_of_blocks</c>, <c>byte sync_complete</c>, <c>u16 len</c>, <c>len*byte encoded_short_ids</c>
/// (BOLT 7).
/// </summary>
public class ReplyChannelRangePayloadSerializer : IPayloadSerializer<ReplyChannelRangePayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public ReplyChannelRangePayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not ReplyChannelRangePayload replyPayload)
            throw new SerializationException($"Payload is not of type {nameof(ReplyChannelRangePayload)}");

        var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                               ?? throw new SerializationException(
                                      $"No serializer found for value object type {nameof(ChainHash)}");
        await chainHashSerializer.SerializeAsync(replyPayload.ChainHash, stream);

        await GossipQueryFieldSerializer.WriteU32Async(replyPayload.FirstBlocknum, stream);
        await GossipQueryFieldSerializer.WriteU32Async(replyPayload.NumberOfBlocks, stream);
        await GossipQueryFieldSerializer.WriteBoolByteAsync(replyPayload.SyncComplete, stream);
        await GossipQueryFieldSerializer.WriteU16PrefixedBytesAsync(replyPayload.EncodedShortIds, stream);
    }

    public async Task<ReplyChannelRangePayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var chainHashSerializer = _valueObjectSerializerFactory.GetSerializer<ChainHash>()
                                   ?? throw new SerializationException(
                                          $"No serializer found for value object type {nameof(ChainHash)}");
            var chainHash = await chainHashSerializer.DeserializeAsync(stream);

            var firstBlocknum = await GossipQueryFieldSerializer.ReadU32Async(stream);
            var numberOfBlocks = await GossipQueryFieldSerializer.ReadU32Async(stream);
            var syncComplete = await GossipQueryFieldSerializer.ReadBoolByteAsync(stream);
            var encodedShortIds = await GossipQueryFieldSerializer.ReadU16PrefixedBytesAsync(stream);

            return new ReplyChannelRangePayload(chainHash, firstBlocknum, numberOfBlocks, syncComplete,
                                                encodedShortIds);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(ReplyChannelRangePayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}