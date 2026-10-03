namespace NLightning.Infrastructure.Serialization.Payloads;

using Domain.Protocol.Payloads;

/// <summary>
/// Serializer for <see cref="AnnouncementSignaturesPayload"/> (BOLT 7 announcement_signatures): frames the Domain codec.
/// The trailing bytes are validated as a TLV extension by
/// <see cref="Messages.Types.AnnouncementSignaturesMessageTypeSerializer"/>.
/// </summary>
public sealed class AnnouncementSignaturesPayloadSerializer
    : GossipCodecPayloadSerializer<AnnouncementSignaturesPayload>
{
    protected override int MinLength => AnnouncementSignaturesPayload.MinLength;

    protected override byte[] GetBytes(AnnouncementSignaturesPayload payload) => payload.GetBytes();

    protected override AnnouncementSignaturesPayload Parse(byte[] data) => AnnouncementSignaturesPayload.Parse(data);
}