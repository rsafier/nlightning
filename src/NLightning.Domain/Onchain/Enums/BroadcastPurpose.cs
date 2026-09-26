namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// What a transaction we broadcast is for (persisted as a byte: never renumber).
/// </summary>
public enum BroadcastPurpose : byte
{
    /// <summary>Not recorded (a caller that predates the purpose, such as <c>PublishAndWatchTransactionAsync</c>).</summary>
    Unspecified = 0,

    /// <summary>A channel funding transaction we funded.</summary>
    Funding = 1,

    /// <summary>A mutual close transaction.</summary>
    MutualClose = 2,

    /// <summary>Our own commitment transaction (fail the channel).</summary>
    LocalCommitment = 3,

    /// <summary>An HTLC-timeout or HTLC-success transaction of our commitment.</summary>
    HtlcTransaction = 4,

    /// <summary>A sweep of an output that pays us (to_local after its delay, to_remote, a second-level output).</summary>
    Sweep = 5,

    /// <summary>A claim of an HTLC output of the peer's commitment (timeout or preimage).</summary>
    HtlcClaim = 6,

    /// <summary>A penalty (justice) transaction spending a revoked commitment or its second-level outputs.</summary>
    Penalty = 7
}