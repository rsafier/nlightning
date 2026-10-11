namespace NLightning.Domain.Gossip.Models;

/// <summary>
/// The gossip share of one connection's outbox (NL-360): what waits to be sent, its caps, and how much gossip the
/// connection took off the queue so far (its progress).
/// </summary>
/// <param name="QueuedMessages">Gossip messages queued and not sent yet.</param>
/// <param name="QueuedBytes">Their size, as given when they were queued.</param>
/// <param name="MaxMessages">The message cap, 0 when there is none.</param>
/// <param name="MaxBytes">The byte cap, 0 when there is none.</param>
/// <param name="SentMessages">Gossip messages taken off the queue for sending since the connection started.</param>
public readonly record struct GossipOutboxDepth(int QueuedMessages, long QueuedBytes, int MaxMessages, long MaxBytes,
                                                long SentMessages)
{
    /// <summary>True when the outbox refuses capped gossip at some depth.</summary>
    public bool IsCapped => MaxMessages > 0 || MaxBytes > 0;

    /// <summary>
    /// True when the queued messages and bytes are both at or below <paramref name="percent"/> % of their caps (a cap
    /// that is off never holds it back).
    /// </summary>
    public bool IsAtOrBelow(int percent) =>
        (MaxMessages <= 0 || QueuedMessages * 100L <= (long)MaxMessages * percent)
     && (MaxBytes <= 0 || QueuedBytes * 100L <= MaxBytes * percent);
}