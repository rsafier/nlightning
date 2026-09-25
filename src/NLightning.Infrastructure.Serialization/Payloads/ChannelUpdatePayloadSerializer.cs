using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// Serializer for <see cref="ChannelUpdatePayload"/> (BOLT 7 channel_update).
/// </summary>
/// <remarks>
/// The field layout lives in <see cref="ChannelUpdatePayload"/> (the Domain also needs it for the signature hash and
/// for BOLT 4 UPDATE failures), so this serializer only frames it: it writes <see cref="ChannelUpdatePayload.GetBytes"/>
/// and reads every byte left in the message, keeping unknown trailing fields (covered by the signature).
/// </remarks>
public class ChannelUpdatePayloadSerializer : IPayloadSerializer<ChannelUpdatePayload>
{
    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not ChannelUpdatePayload channelUpdatePayload)
            throw new SerializationException($"Payload is not of type {nameof(ChannelUpdatePayload)}");

        await stream.WriteAsync(channelUpdatePayload.GetBytes());
    }

    public async Task<ChannelUpdatePayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var remaining = stream.Length - stream.Position;
            if (remaining < ChannelUpdatePayload.MinLength)
                throw new SerializationException(
                    $"A channel_update payload is at least {ChannelUpdatePayload.MinLength} bytes, got {remaining}");

            var data = new byte[remaining];
            await stream.ReadExactlyAsync(data);

            return ChannelUpdatePayload.Parse(data);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(ChannelUpdatePayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}