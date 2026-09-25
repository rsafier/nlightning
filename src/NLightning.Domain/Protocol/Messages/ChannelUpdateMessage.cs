namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a channel_update message (BOLT 7, type 258). The payload is kept as raw bytes.
/// </summary>
public sealed class ChannelUpdateMessage(GossipPayload payload) : GossipMessage(MessageTypes.ChannelUpdate, payload);