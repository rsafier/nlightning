namespace NLightning.Application.OnionMessages;

/// <summary>
/// BOLT 4 onion messages (wave M6), bound from <see cref="SectionName"/>. Whether the node takes part at all is the
/// feature bit (<c>Node:Features:OptionOnionMessages</c>, experimental until Proof M6); these knobs bound what it does.
/// </summary>
public sealed class OnionMessageOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "OnionMessages";

    /// <summary>
    /// The most onion messages queued on one peer's outbox; more are dropped (default 64). Onion messages never delay
    /// channel messages beyond FIFO order, and a slow reader only loses onion messages.
    /// </summary>
    public int MaxOutboxPerPeer { get; set; } = 64;

    /// <summary>
    /// The incoming onion messages waiting to be peeled; more are dropped (default 1024).
    /// </summary>
    public int MaxQueuedMessages { get; set; } = 1024;

    /// <summary>
    /// The delivered messages waiting for their handler; more are dropped (default 256).
    /// </summary>
    public int MaxQueuedHandlerWork { get; set; } = 256;

    /// <summary>
    /// The replies we wait for at once (<c>SendAndWaitForReplyAsync</c>); a send over the limit is refused as dropped
    /// (default 1024).
    /// </summary>
    public int MaxPendingReplies { get; set; } = 1024;

    /// <summary>
    /// How long <c>SendAndWaitForReplyAsync</c> callers are told to wait by default (default 30 seconds).
    /// </summary>
    public TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The most unblinded hops we prepend to reach a destination or an introduction node through the graph (default
    /// 3). A connected peer is always tried first.
    /// </summary>
    public int MaxPathHops { get; set; } = 3;

    /// <summary>The default of <see cref="BlindedPathDummyHops"/>.</summary>
    public const int DefaultBlindedPathDummyHops = 1;

    /// <summary>The most dummy hops <see cref="BlindedPathDummyHops"/> may ask for.</summary>
    public const int MaxBlindedPathDummyHops = 4;

    /// <summary>
    /// How many dummy hops the blinded paths we hand out end with (BOLT 4: the writer MAY add dummy hops at the end of
    /// the path, which it will ignore on receipt): our reply paths (<c>SendAndWaitForReplyAsync</c>) and the
    /// <c>offer_paths</c> of <c>createoffer</c>. Each dummy is a hop of our own node that relays to itself
    /// (<c>next_node_id</c> = us), padded like every other hop, so a sender sees <c>2 + BlindedPathDummyHops</c> hops
    /// that look like one through <c>BlindedPathDummyHops</c> more nodes; we peel them ourselves on receipt (NL-525).
    /// Default 1, like the blinded payment paths (<c>Node:Invoices:BlindedPathDummyHops</c>); 0 turns them off; at
    /// most <see cref="MaxBlindedPathDummyHops"/>. A value outside 0 to that is a configuration error, which keeps
    /// onion messages (and with them offers) off.
    /// </summary>
    public int BlindedPathDummyHops { get; set; } = DefaultBlindedPathDummyHops;

    /// <summary>
    /// Per peer: bytes per second admitted (default 64 KiB/s). Over the limit, a message is dropped (BOLT 4 MAY).
    /// </summary>
    public int PeerBytesPerSecond { get; set; } = 64 * 1024;

    /// <summary>Per peer: the byte burst (default 256 KiB).</summary>
    public int PeerBurstBytes { get; set; } = 256 * 1024;

    /// <summary>Per peer: messages per second admitted (default 20).</summary>
    public int PeerMessagesPerSecond { get; set; } = 20;

    /// <summary>Per peer: the message burst (default 20).</summary>
    public int PeerBurstMessages { get; set; } = 20;

    /// <summary>All peers together: bytes per second admitted (default 640 KiB/s). Over the limit, a message is
    /// dropped (BOLT 4 MAY).</summary>
    public int GlobalBytesPerSecond { get; set; } = 640 * 1024;

    /// <summary>All peers together: the byte burst (default 1,600 KiB, LND's global burst).</summary>
    public int GlobalBurstBytes { get; set; } = 1600 * 1024;

    /// <summary>All peers together: messages per second admitted (default 200).</summary>
    public int GlobalMessagesPerSecond { get; set; } = 200;

    /// <summary>All peers together: the message burst (default 200).</summary>
    public int GlobalBurstMessages { get; set; } = 200;

    /// <summary>
    /// Open a connection to reply or to forward (plan D6). Not implemented: must stay false.
    /// </summary>
    public bool ConnectToReply { get; set; }

    /// <summary>
    /// The configuration errors, empty when valid.
    /// </summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();
        if (MaxOutboxPerPeer < 1)
            errors.Add($"{SectionName}:{nameof(MaxOutboxPerPeer)} must be at least 1.");
        if (MaxQueuedMessages < 1)
            errors.Add($"{SectionName}:{nameof(MaxQueuedMessages)} must be at least 1.");
        if (MaxQueuedHandlerWork < 1)
            errors.Add($"{SectionName}:{nameof(MaxQueuedHandlerWork)} must be at least 1.");
        if (MaxPendingReplies < 1)
            errors.Add($"{SectionName}:{nameof(MaxPendingReplies)} must be at least 1.");
        if (ReplyTimeout <= TimeSpan.Zero)
            errors.Add($"{SectionName}:{nameof(ReplyTimeout)} must be positive.");
        if (MaxPathHops is < 0 or > 16)
            errors.Add($"{SectionName}:{nameof(MaxPathHops)} must be 0 to 16.");
        if (BlindedPathDummyHops is < 0 or > MaxBlindedPathDummyHops)
            errors.Add($"{SectionName}:{nameof(BlindedPathDummyHops)} must be 0 to {MaxBlindedPathDummyHops}.");
        if (PeerBytesPerSecond < 1 || PeerBurstBytes < 1 || PeerMessagesPerSecond < 1 || PeerBurstMessages < 1
         || GlobalBytesPerSecond < 1 || GlobalBurstBytes < 1 || GlobalMessagesPerSecond < 1 || GlobalBurstMessages < 1)
            errors.Add($"{SectionName}: the rate limits must be at least 1.");
        if (PeerBurstBytes < PeerBytesPerSecond)
            errors.Add($"{SectionName}:{nameof(PeerBurstBytes)} must be at least {nameof(PeerBytesPerSecond)}.");
        if (GlobalBurstBytes < GlobalBytesPerSecond)
            errors.Add($"{SectionName}:{nameof(GlobalBurstBytes)} must be at least {nameof(GlobalBytesPerSecond)}.");
        if (ConnectToReply)
            errors.Add($"{SectionName}:{nameof(ConnectToReply)} is not implemented; leave it false.");
        return errors;
    }
}