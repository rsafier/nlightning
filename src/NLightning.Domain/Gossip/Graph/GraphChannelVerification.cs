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
    Own = 2
}