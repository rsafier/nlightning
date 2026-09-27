using System.Buffers;
using System.Runtime.Serialization;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.Constants;
using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// <c>splice_locked</c> (type 77): <c>channel_id</c> ‖ <c>sha256 splice_txid</c> (SP-LK-01).
/// </summary>
public class SpliceLockedPayloadSerializer : IPayloadSerializer<SpliceLockedPayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public SpliceLockedPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not SpliceLockedPayload spliceLockedPayload)
            throw new SerializationException($"Payload is not of type {nameof(SpliceLockedPayload)}");

        var channelIdSerializer =
            _valueObjectSerializerFactory.GetSerializer<ChannelId>()
         ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
        await channelIdSerializer.SerializeAsync(spliceLockedPayload.ChannelId, stream);

        await stream.WriteAsync(spliceLockedPayload.SpliceTxId);
    }

    public async Task<SpliceLockedPayload?> DeserializeAsync(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CryptoConstants.Sha256HashLen);

        try
        {
            var channelIdSerializer =
                _valueObjectSerializerFactory.GetSerializer<ChannelId>()
             ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
            var channelId = await channelIdSerializer.DeserializeAsync(stream);

            await stream.ReadExactlyAsync(buffer.AsMemory()[..CryptoConstants.Sha256HashLen]);
            var spliceTxId = new TxId(buffer[..CryptoConstants.Sha256HashLen]);

            return new SpliceLockedPayload(channelId, spliceTxId);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(SpliceLockedPayload)}", e);
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