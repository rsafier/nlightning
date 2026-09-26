namespace NLightning.Domain.Onchain.Enums;

/// <summary>
/// What spent a channel's funding output (BOLT 5 plan §3.1, O2-T3). Persisted as a byte later (close record); never
/// renumber.
/// </summary>
public enum FundingSpendKind : byte
{
    /// <summary>
    /// A spend that is none of the cases below (B5-GEN-06): the user must be warned of potentially lost funds.
    /// </summary>
    Unknown = 0,

    /// <summary>A mutual close (B5-MUT-01).</summary>
    Mutual = 1,

    /// <summary>Our current local commitment (B5-LCL-*).</summary>
    LocalCommit = 2,

    /// <summary>The peer's current (unrevoked) commitment (B5-RMT-*).</summary>
    RemoteCommit = 3,

    /// <summary>The peer's commitment we signed whose <c>revoke_and_ack</c> is outstanding (B5-RMT-01).</summary>
    RemoteNextCommit = 4,

    /// <summary>A peer commitment it revoked: a breach (B5-REV-*).</summary>
    Revoked = 5,

    /// <summary>A peer commitment newer than any we know: we lost data (B5-RMT-03).</summary>
    FutureRemote = 6,

    /// <summary>The transaction does not spend the funding output at all (a caller error, never a close).</summary>
    NotFundingSpend = 7
}