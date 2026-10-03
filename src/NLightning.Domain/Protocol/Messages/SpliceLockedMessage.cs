namespace NLightning.Domain.Protocol.Messages;

using Constants;
using Payloads;

/// <summary>
/// Represents a splice_locked message (BOLT 2 "Channel Splicing", type 77, SP-LK-01).
/// </summary>
/// <remarks>
/// Sent when a splice transaction reaches acceptable depth; the splice completes when both sides sent it for the same
/// txid (SP-LK-03). No TLVs. Handled in wave SP2 (lane SP2-B); the wire is lane SP1-A's.
/// </remarks>
/// <param name="payload">The splice_locked payload.</param>
public sealed class SpliceLockedMessage(SpliceLockedPayload payload)
    : BaseChannelMessage(MessageTypes.SpliceLocked, payload)
{
    /// <summary>
    /// The payload of the message.
    /// </summary>
    public new SpliceLockedPayload Payload { get => (SpliceLockedPayload)base.Payload; }
}