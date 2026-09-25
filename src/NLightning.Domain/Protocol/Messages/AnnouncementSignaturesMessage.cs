namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a announcement_signatures message (BOLT 7, type 259). The payload is kept as raw bytes.
/// </summary>
public sealed class AnnouncementSignaturesMessage(GossipPayload payload) : GossipMessage(MessageTypes.AnnouncementSignatures, payload);