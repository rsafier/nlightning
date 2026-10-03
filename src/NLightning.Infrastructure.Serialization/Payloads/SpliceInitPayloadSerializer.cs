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
/// <c>splice_init</c> (type 80): <c>channel_id</c> ‖ <c>s64 funding_contribution_satoshis</c> ‖
/// <c>u32 funding_feerate_perkw</c> ‖ <c>u32 locktime</c> ‖ <c>point funding_pubkey</c> (SP-W-01).
/// </summary>
public class SpliceInitPayloadSerializer : IPayloadSerializer<SpliceInitPayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public SpliceInitPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not SpliceInitPayload spliceInitPayload)
            throw new SerializationException($"Payload is not of type {nameof(SpliceInitPayload)}");

        var channelIdSerializer =
            _valueObjectSerializerFactory.GetSerializer<ChannelId>()
         ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
        await channelIdSerializer.SerializeAsync(spliceInitPayload.ChannelId, stream);

        var fields = new byte[sizeof(long) + sizeof(uint) + sizeof(uint)];
        BinaryPrimitives.WriteInt64BigEndian(fields, spliceInitPayload.FundingContributionSatoshis);
        BinaryPrimitives.WriteUInt32BigEndian(fields.AsSpan(sizeof(long)), spliceInitPayload.FundingFeeratePerKw);
        BinaryPrimitives.WriteUInt32BigEndian(fields.AsSpan(sizeof(long) + sizeof(uint)), spliceInitPayload.Locktime);
        await stream.WriteAsync(fields);

        await stream.WriteAsync(spliceInitPayload.FundingPubKey);
    }

    public async Task<SpliceInitPayload?> DeserializeAsync(Stream stream)
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

            await stream.ReadExactlyAsync(buffer.AsMemory()[..sizeof(uint)]);
            var feeratePerKw = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, sizeof(uint)));

            await stream.ReadExactlyAsync(buffer.AsMemory()[..sizeof(uint)]);
            var locktime = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, sizeof(uint)));

            await stream.ReadExactlyAsync(buffer.AsMemory()[..CryptoConstants.CompactPubkeyLen]);
            var fundingPubKey = new CompactPubKey(buffer[..CryptoConstants.CompactPubkeyLen]);

            return new SpliceInitPayload(channelId, contribution, feeratePerKw, locktime, fundingPubKey);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(SpliceInitPayload)}", e);
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