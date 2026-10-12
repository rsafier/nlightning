namespace NLightning.Domain.Protocol.OnionMessages.Enums;

/// <summary>
/// The outcome of sending an onion message. There are no error replies to onion messages (BOLT 4), so
/// <see cref="Sent"/> only means the message was queued to the first hop.
/// </summary>
public enum OnionMessageSendStatus
{
    /// <summary>
    /// Queued to the first hop's connection.
    /// </summary>
    Sent = 0,

    /// <summary>
    /// Sent, and the expected reply arrived through our reply path.
    /// </summary>
    Replied = 1,

    /// <summary>
    /// Onion messages are off: <c>option_onion_messages</c> is not advertised, or no service is registered.
    /// </summary>
    NotAvailable = 2,

    /// <summary>
    /// No path to the destination or to its introduction node (for example an unresolvable <c>sciddir</c>, or no
    /// connected first hop: we never connect to send).
    /// </summary>
    NoPath = 3,

    /// <summary>
    /// The contents do not fit the largest packet (32768 bytes of payloads).
    /// </summary>
    TooLarge = 4,

    /// <summary>
    /// The first hop's connection dropped the message (disconnected, or its onion-message outbox is full).
    /// </summary>
    Dropped = 5,

    /// <summary>
    /// Sent, but no expected reply arrived before the deadline.
    /// </summary>
    ReplyTimedOut = 6
}