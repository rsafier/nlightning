using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.Serialization;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// <c>splice_ack</c> (type 81): <c>channel_id</c> ‖ <c>s64 funding_contribution_satoshis</c> ‖
/// <c>point funding_pubkey</c> (SP-W-02).
/// </summary>
public class SpliceAckPayloadSerializer : IPayloadSerializer<SpliceAckPayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public SpliceAckPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not SpliceAckPayload spliceAckPayload)
            throw new SerializationException($"Payload is not of type {nameof(SpliceAckPayload)}");

        var channelIdSerializer =
            _valueObjectSerializerFactory.GetSerializer<ChannelId>()
         ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
        await channelIdSerializer.SerializeAsync(spliceAckPayload.ChannelId, stream);

        var contribution = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(contribution, spliceAckPayload.FundingContributionSatoshis);
        await stream.WriteAsync(contribution);

        await stream.WriteAsync(spliceAckPayload.FundingPubKey);
    }

    public async Task<SpliceAckPayload?> DeserializeAsync(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CryptoConstants.CompactPubkeyLen);

        try
        {
            var channelIdSerializer =
                _valueObjectSerializerFactory.GetSerializer<ChannelId>()
             ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
            var channelId = await channelIdSerializer.DeserializeAsync(stream);

            await stream.ReadExactlyAsync(buffer.AsMemory()[..sizeof(long)]);
            var contribution = BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(0, sizeof(long)));

            await stream.ReadExactlyAsync(buffer.AsMemory()[..CryptoConstants.CompactPubkeyLen]);
            var fundingPubKey = new CompactPubKey(buffer[..CryptoConstants.CompactPubkeyLen]);

            return new SpliceAckPayload(channelId, contribution, fundingPubKey);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(SpliceAckPayload)}", e);
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