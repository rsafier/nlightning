namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;

/// <summary>
/// Serializer for <see cref="NodeAnnouncementPayload"/> (BOLT 7 node_announcement): frames the Domain codec.
/// </summary>
public sealed class NodeAnnouncementPayloadSerializer : GossipCodecPayloadSerializer<NodeAnnouncementPayload>
{
    protected override int MinLength => NodeAnnouncementPayload.MinLength;

    protected override byte[] GetBytes(NodeAnnouncementPayload payload) => payload.GetBytes();

    protected override NodeAnnouncementPayload Parse(byte[] data) => NodeAnnouncementPayload.Parse(data);
}