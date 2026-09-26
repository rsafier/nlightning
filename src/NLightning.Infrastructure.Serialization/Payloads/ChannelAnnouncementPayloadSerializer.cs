namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;

/// <summary>
/// Serializer for <see cref="ChannelAnnouncementPayload"/> (BOLT 7 channel_announcement): frames the Domain codec.
/// </summary>
public sealed class ChannelAnnouncementPayloadSerializer : GossipCodecPayloadSerializer<ChannelAnnouncementPayload>
{
    protected override int MinLength => ChannelAnnouncementPayload.MinLength;

    protected override byte[] GetBytes(ChannelAnnouncementPayload payload) => payload.GetBytes();

    protected override ChannelAnnouncementPayload Parse(byte[] data) => ChannelAnnouncementPayload.Parse(data);
}