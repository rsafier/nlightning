using System.Buffers.Binary;
using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// The payload of <c>onion_message</c> (type 513, BOLT 4 "Onion Messages"):
/// <c>point path_key || u16 len || len*byte onion_message_packet</c>.
/// </summary>
/// <remarks>
/// Any <c>len</c> from <see cref="OnionConstants.PacketOverheadLength"/> (66) up is read: only the writer SHOULD use
/// 1366 or 32834. A shorter <c>len</c>, a key without a 02/03 prefix or a truncated packet throws
/// <see cref="PayloadSerializationException"/>; the message service ignores such a 513 and keeps the connection,
/// without a warning (NL-444).
/// </remarks>
public class OnionMessagePayloadSerializer : IPayloadSerializer<OnionMessagePayload>
{
    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not OnionMessagePayload onionMessagePayload)
            throw new SerializationException($"Payload is not of type {nameof(OnionMessagePayload)}");

        var header = new byte[CryptoConstants.CompactPubkeyLen + sizeof(ushort)];
        ((ReadOnlySpan<byte>)onionMessagePayload.PathKey).CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(CryptoConstants.CompactPubkeyLen),
                                              (ushort)onionMessagePayload.OnionMessagePacket.Length);

        await stream.WriteAsync(header);
        await stream.WriteAsync(onionMessagePayload.OnionMessagePacket);
    }

    public async Task<OnionMessagePayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var header = new byte[CryptoConstants.CompactPubkeyLen + sizeof(ushort)];
            await stream.ReadExactlyAsync(header);

            CompactPubKey pathKey = header.AsSpan(0, CryptoConstants.CompactPubkeyLen);
            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(CryptoConstants.CompactPubkeyLen));

            if (length < OnionConstants.PacketOverheadLength)
                throw new SerializationException(
                    $"onion_message len {length} is below the {OnionConstants.PacketOverheadLength}-byte packet "
                  + "overhead");
            if (stream.Length - stream.Position < length)
                throw new SerializationException(
                    $"onion_message_packet is truncated: expected {length} bytes, "
                  + $"{stream.Length - stream.Position} left");

            var packet = new byte[length];
            await stream.ReadExactlyAsync(packet);

            return new OnionMessagePayload(pathKey, packet);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(OnionMessagePayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}