namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a channel_announcement message (BOLT 7, type 256). The payload is kept as raw bytes.
/// </summary>
public sealed class ChannelAnnouncementMessage(GossipPayload payload) : GossipMessage(MessageTypes.ChannelAnnouncement, payload);