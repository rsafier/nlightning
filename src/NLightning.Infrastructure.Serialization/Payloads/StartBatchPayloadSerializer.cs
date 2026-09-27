using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Serialization;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// <c>start_batch</c> (type 127): <c>channel_id</c> ‖ <c>u16 batch_size</c> (BOLT 2 "Batching channel messages").
/// </summary>
/// <remarks>
/// Any <c>batch_size</c> is parsed: the receiver rules on its value (ignore at &lt;= 1, warn and close above 20,
/// SP-OP-04) are the inbound loop's.
/// </remarks>
public class StartBatchPayloadSerializer : IPayloadSerializer<StartBatchPayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public StartBatchPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not StartBatchPayload startBatchPayload)
            throw new SerializationException($"Payload is not of type {nameof(StartBatchPayload)}");

        var channelIdSerializer =
            _valueObjectSerializerFactory.GetSerializer<ChannelId>()
         ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
        await channelIdSerializer.SerializeAsync(startBatchPayload.ChannelId, stream);

        var batchSize = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(batchSize, startBatchPayload.BatchSize);
        await stream.WriteAsync(batchSize);
    }

    public async Task<StartBatchPayload?> DeserializeAsync(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(ushort));

        try
        {
            var channelIdSerializer =
                _valueObjectSerializerFactory.GetSerializer<ChannelId>()
             ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
            var channelId = await channelIdSerializer.DeserializeAsync(stream);

            await stream.ReadExactlyAsync(buffer.AsMemory()[..sizeof(ushort)]);
            var batchSize = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, sizeof(ushort)));

            return new StartBatchPayload(channelId, batchSize);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(StartBatchPayload)}", e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}