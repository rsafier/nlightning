using System.Buffers;
using System.Runtime.Serialization;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Converters;
using Domain.Crypto.Constants;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Payloads;
using Exceptions;

public class UpdateAddHtlcPayloadSerializer : IPayloadSerializer<UpdateAddHtlcPayload>
{
    private readonly IValueObjectSerializerFactory _valueObjectSerializerFactory;

    public UpdateAddHtlcPayloadSerializer(IValueObjectSerializerFactory valueObjectSerializerFactory)
    {
        _valueObjectSerializerFactory = valueObjectSerializerFactory;
    }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not UpdateAddHtlcPayload updateAddHtlcPayload)
            throw new SerializationException($"Payload is not of type {nameof(UpdateAddHtlcPayload)}");

        // Get the ChannelId serializer
        var channelIdSerializer =
            _valueObjectSerializerFactory.GetSerializer<ChannelId>()
         ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
        await channelIdSerializer.SerializeAsync(updateAddHtlcPayload.ChannelId, stream);

        await stream.WriteAsync(EndianBitConverter.GetBytesBigEndian(updateAddHtlcPayload.Id));
        await stream.WriteAsync(EndianBitConverter.GetBytesBigEndian(updateAddHtlcPayload.Amount.MilliSatoshi));
        await stream.WriteAsync(updateAddHtlcPayload.PaymentHash);
        await stream.WriteAsync(EndianBitConverter.GetBytesBigEndian(updateAddHtlcPayload.CltvExpiry));
        if (updateAddHtlcPayload.OnionRoutingPacket.Length != OnionConstants.PacketLength)
            throw new SerializationException(
                $"Onion routing packet must be exactly {OnionConstants.PacketLength} bytes");
        await stream.WriteAsync(updateAddHtlcPayload.OnionRoutingPacket);
    }

    public async Task<UpdateAddHtlcPayload?> DeserializeAsync(Stream stream)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(sizeof(ulong));

        try
        {
            // Get the ChannelId serializer
            var channelIdSerializer =
                _valueObjectSerializerFactory.GetSerializer<ChannelId>()
             ?? throw new SerializationException($"No serializer found for value object type {nameof(ChannelId)}");
            var channelId = await channelIdSerializer.DeserializeAsync(stream);

            await stream.ReadExactlyAsync(buffer.AsMemory()[..sizeof(ulong)]);
            var id = EndianBitConverter.ToUInt64BigEndian(buffer[..sizeof(ulong)]);

            await stream.ReadExactlyAsync(buffer.AsMemory()[..sizeof(ulong)]);
            var amountMsat = EndianBitConverter.ToUInt64BigEndian(buffer[..sizeof(ulong)]);

            var paymentHash = new byte[CryptoConstants.Sha256HashLen];
            await stream.ReadExactlyAsync(paymentHash);

            await stream.ReadExactlyAsync(buffer.AsMemory()[..sizeof(uint)]);
            var cltvExpiry = EndianBitConverter.ToUInt32BigEndian(buffer[..sizeof(uint)]);

            // The onion packet is mandatory and fixed-size; a short stream throws EndOfStreamException
            var onionRoutingPacket = new byte[OnionConstants.PacketLength];
            await stream.ReadExactlyAsync(onionRoutingPacket);

            return new UpdateAddHtlcPayload(amountMsat, channelId, cltvExpiry, id, paymentHash, onionRoutingPacket);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(UpdateAddHtlcPayload)}", e);
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