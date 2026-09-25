using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// Serializer for the opaque <see cref="GossipPayload"/>: the payload is every byte left in the message stream.
/// </summary>
public class GossipPayloadSerializer : IPayloadSerializer<GossipPayload>
{
    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not GossipPayload gossipPayload)
            throw new SerializationException($"Payload is not of type {nameof(GossipPayload)}");

        await stream.WriteAsync(gossipPayload.Data);
    }

    public async Task<GossipPayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var remaining = stream.Length - stream.Position;
            var data = new byte[remaining];
            await stream.ReadExactlyAsync(data);

            return new GossipPayload(data);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {nameof(GossipPayload)}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }
}