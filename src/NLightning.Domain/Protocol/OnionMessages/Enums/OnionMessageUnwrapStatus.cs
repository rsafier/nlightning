namespace NLightning.Domain.Protocol.OnionMessages.Enums;

using Interfaces;

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