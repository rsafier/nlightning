namespace NLightning.Application.OnionMessages;

/// <summary>
/// The <c>reason</c> tags of <c>nlightning.onion_messages.dropped</c>.
/// </summary>
public static class OnionMessageDropReasons
{
    /// <summary>option_onion_messages is not advertised.</summary>
    public const string NotAvailable = "not_available";

    /// <summary>Over the rate limit (BOLT 4 MAY).</summary>
    public const string RateLimited = "rate";

    /// <summary>The receive queue is full.</summary>
    public const string QueueFull = "queue";

    /// <summary>The packet does not peel (bad version, key, HMAC or length).</summary>
    public const string Undecryptable = "peel";

    /// <summary>The <c>onionmsg_tlv</c> is invalid or has an unknown even type.</summary>
    public const string InvalidPayload = "payload";

    /// <summary>No <c>encrypted_recipient_data</c>, or it does not decrypt.</summary>
    public const string InvalidRecipientData = "recipient_data";

    /// <summary>The recipient data breaks a message-path rule (allowed_features, payment_relay/constraints).</summary>
    public const string ForbiddenRecipientData = "recipient_data_rules";

    /// <summary>A non-final hop carries more than <c>encrypted_recipient_data</c>.</summary>
    public const string NonFinalExtraFields = "non_final_fields";

    /// <summary>A non-final hop's recipient data has a <c>path_id</c>.</summary>
    public const string NonFinalPathId = "non_final_path_id";

    /// <summary>No next node, or its SCID is not one of ours.</summary>
    public const string NoNextHop = "no_next_hop";

    /// <summary>The next peer is not connected or did not negotiate onion messages (we never connect to forward).
    /// </summary>
    public const string NextPeerUnreachable = "next_peer_unreachable";

    /// <summary>The next peer is the one the message came from.</summary>
    public const string Echo = "echo";

    /// <summary>The next node is us.</summary>
    public const string Loop = "loop";

    /// <summary>
    /// The next peer's outbox refused the message: it already holds its cap of onion messages (a peer that reads
    /// slowly), or the connection is closing.
    /// </summary>
    public const string OutboxFull = "outbox_full";

    /// <summary>A final hop with more than one payload field.</summary>
    public const string MultiplePayloadFields = "multiple_payload_fields";

    /// <summary>A message through one of our reply paths that is not the expected reply.</summary>
    public const string UnexpectedReply = "unexpected_reply";

    /// <summary>No handler for the payload field.</summary>
    public const string NoHandler = "no_handler";

    /// <summary>The handler queue is full.</summary>
    public const string HandlerQueueFull = "handler_queue";

    /// <summary>
    /// The 513 message itself did not parse (NL-444): ignored by the transport's message service, which counts it on
    /// the same meter and instrument; it never reaches the service, so <see cref="OnionMessageMetrics"/>' in-memory
    /// counts do not include it.
    /// </summary>
    public const string Malformed = "malformed";
}