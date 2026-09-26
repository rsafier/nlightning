namespace NLightning.Domain.Gossip.Persistence;

/// <summary>
/// How a stored graph channel was checked against the chain (BOLT 7 plan §3.4). Persisted as a byte: never renumber.
/// </summary>
public enum GraphChannelVerification : byte
{
    /// <summary>The funding output was found unspent with the announced keys and its amount is the capacity.</summary>
    Verified = 0,

    /// <summary>
    /// The funding block could not be read (pruned bitcoind, <c>Gossip:FundingValidation = SkipUnavailable</c>): kept
    /// for routing with a penalty, never relayed.
    /// </summary>
    Unverified = 1,

    /// <summary>One of our own channels: announced by us, no chain lookup needed.</summary>
    Own = 2
}