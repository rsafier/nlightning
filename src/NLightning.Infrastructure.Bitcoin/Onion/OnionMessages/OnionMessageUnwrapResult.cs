namespace NLightning.Infrastructure.Bitcoin.Onion.OnionMessages;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.OnionMessages;

/// <summary>
/// What a node does with a received <c>onion_message</c> after <see cref="IOnionMessageUnwrapper"/>.
/// </summary>
public enum OnionMessageUnwrapStatus
{
    /// <summary>
    /// Ignore the message (BOLT 4 reader "MUST ignore"); nothing is sent back.
    /// </summary>
    Ignored,

    /// <summary>
    /// Forward <see cref="OnionMessageUnwrapResult.NextMessage"/> to the next peer.
    /// </summary>
    Forward,

    /// <summary>
    /// We are the final hop: deliver <see cref="OnionMessageUnwrapResult.Payload"/>.
    /// </summary>
    Deliver
}

/// <summary>
/// The result of unwrapping one received <c>onion_message</c> (BOLT 4 "Onion Messages", reader).
/// </summary>
/// <remarks>
/// <para><see cref="OnionMessageUnwrapStatus.Forward"/>: <see cref="NextNodeId"/> or, when the data names a channel
/// instead, <see cref="NextShortChannelId"/> (a real SCID or a local alias, resolved by the caller) is the next peer.
/// The next node id may be our own (we are the introduction node of a path the sender joined through us): the caller
/// unwraps <see cref="NextMessage"/> again.</para>
/// <para><see cref="OnionMessageUnwrapStatus.Deliver"/>: <see cref="Payload"/> is the final <c>onionmsg_tlv</c> (at
/// most one payload field) and <see cref="RecipientData"/> our decrypted data, whose <c>path_id</c> names the
/// path of ours the message came through (null when the sender built the path to us).</para>
/// </remarks>
public sealed record OnionMessageUnwrapResult
{
    /// <summary>What to do with the message.</summary>
    public OnionMessageUnwrapStatus Status { get; private init; }

    /// <summary>Why the message is ignored (for logs and metrics), or null.</summary>
    public string? IgnoreReason { get; private init; }

    /// <summary>The decrypted <c>encrypted_data_tlv</c> of this hop, or null when ignored.</summary>
    public BlindedRecipientData? RecipientData { get; private init; }

    /// <summary>Forward: the next peer's node id, or null when <see cref="NextShortChannelId"/> names it.</summary>
    public CompactPubKey? NextNodeId { get; private init; }

    /// <summary>Forward: the channel to the next peer, when the data has no <c>next_node_id</c>.</summary>
    public ShortChannelId? NextShortChannelId { get; private init; }

    /// <summary>Forward: the <c>onion_message</c> to send, with the next <c>path_key</c>.</summary>
    public OnionMessageMessage? NextMessage { get; private init; }

    /// <summary>Deliver: the final hop's <c>onionmsg_tlv</c>.</summary>
    public OnionMessageTlvs? Payload { get; private init; }

    /// <summary>Deliver: the <c>path_id</c> of our data, or null.</summary>
    public ReadOnlyMemory<byte>? PathId => Status == OnionMessageUnwrapStatus.Deliver ? RecipientData?.PathId : null;

    internal static OnionMessageUnwrapResult Ignore(string reason) =>
        new() { Status = OnionMessageUnwrapStatus.Ignored, IgnoreReason = reason };

    internal static OnionMessageUnwrapResult Forward(BlindedRecipientData recipientData, OnionMessageMessage next) =>
        new()
        {
            Status = OnionMessageUnwrapStatus.Forward,
            RecipientData = recipientData,
            NextNodeId = recipientData.NextNodeId,
            NextShortChannelId = recipientData.NextNodeId is null ? recipientData.ShortChannelId : null,
            NextMessage = next
        };

    internal static OnionMessageUnwrapResult Deliver(BlindedRecipientData recipientData, OnionMessageTlvs payload) =>
        new() { Status = OnionMessageUnwrapStatus.Deliver, RecipientData = recipientData, Payload = payload };
}