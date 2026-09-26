using System.Runtime.Serialization;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;

namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;
using Exceptions;

/// <summary>
/// Base serializer for the BOLT 7 payloads whose field layout lives in the Domain payload itself
/// (<see cref="ChannelAnnouncementPayload"/>, <see cref="NodeAnnouncementPayload"/>,
/// <see cref="AnnouncementSignaturesPayload"/>; plan decision D1, the <see cref="ChannelUpdatePayloadSerializer"/>
/// pattern).
/// </summary>
/// <remarks>
/// The serializer only frames the Domain codec: it writes the payload's wire bytes and reads every byte left in the
/// (one-message) stream, so unknown trailing fields stay in the payload and parse → serialize is byte-identical. Any
/// malformed payload (too short, a length prefix past the end, an invalid key encoding) throws
/// <see cref="PayloadSerializationException"/>, which the peer answers with a warning and a disconnect (NL-207).
/// </remarks>
/// <typeparam name="TPayload">The payload type.</typeparam>
public abstract class GossipCodecPayloadSerializer<TPayload> : IPayloadSerializer<TPayload>
    where TPayload : class, IMessagePayload
{
    /// <summary>
    /// The minimum length of the payload (without the message type).
    /// </summary>
    protected abstract int MinLength { get; }

    public async Task SerializeAsync(IMessagePayload payload, Stream stream)
    {
        if (payload is not TPayload typedPayload)
            throw new SerializationException($"Payload is not of type {typeof(TPayload).Name}");

        await stream.WriteAsync(GetBytes(typedPayload));
    }

    public async Task<TPayload?> DeserializeAsync(Stream stream)
    {
        try
        {
            var remaining = stream.Length - stream.Position;
            if (remaining < MinLength)
                throw new SerializationException(
                    $"A {typeof(TPayload).Name} is at least {MinLength} bytes, got {remaining}");

            var data = new byte[remaining];
            await stream.ReadExactlyAsync(data);

            return Parse(data);
        }
        catch (Exception e)
        {
            throw new PayloadSerializationException($"Error deserializing {typeof(TPayload).Name}", e);
        }
    }

    async Task<IMessagePayload?> IPayloadSerializer.DeserializeAsync(Stream stream)
    {
        return await DeserializeAsync(stream);
    }

    /// <summary>
    /// The wire bytes of <paramref name="payload"/>.
    /// </summary>
    protected abstract byte[] GetBytes(TPayload payload);

    /// <summary>
    /// Parses the whole payload.
    /// </summary>
    protected abstract TPayload Parse(byte[] data);
}