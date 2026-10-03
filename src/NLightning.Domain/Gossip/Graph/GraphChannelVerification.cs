namespace NLightning.Domain.Gossip.Graph;

/// <summary>
/// How a graph channel's funding output was checked (plan BOLT7 §3.4). Persisted as a byte: never renumber.
/// </summary>
public enum GraphChannelVerification : byte
{
    /// <summary>
    /// The SCID's output was found unspent, a P2WSH of the two bitcoin keys, with at least 6 confirmations.
    /// </summary>
    Verified = 0,

    /// <summary>
    /// The funding block was unavailable (pruned bitcoind, <c>Gossip:FundingValidation=SkipUnavailable</c>): usable
    /// for routing with a probability penalty, never relayed.
    /// </summary>
    Unverified = 1,

    /// <summary>
    /// One of our own channels (no chain lookup needed).
    /// </summary>
    Own = 2,

    /// <summary>
    /// Accepted on its four signatures alone, without the funding output lookup (<c>Gossip:AssumeChannelValid</c>, the
    /// equivalent of LND's <c>--routing.assumechanvalid</c>): the capacity is unknown (routing estimates it from the
    /// policies' <c>htlc_maximum_msat</c>, <see cref="GraphChannel.EstimatedCapacityMsat"/>), a closed channel is only
    /// dropped by the stale rule, and the channel is never relayed or served in query replies (BOLT 7 requires the
    /// output check before a <c>channel_announcement</c> is passed on).
    /// </summary>
    Assumed = 3
}