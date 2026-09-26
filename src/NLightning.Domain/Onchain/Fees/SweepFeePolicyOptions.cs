namespace NLightning.Domain.Onchain.Fees;

/// <summary>
/// Settings of <see cref="SweepFeePolicy"/> (BOLT 5 plan §3.7, O6-T1). The defaults are the plan's; the host binds
/// them from the <c>Onchain</c> configuration section when the policy is wired in.
/// </summary>
public sealed record SweepFeePolicyOptions
{
    /// <summary>The feerate floor (BOLT 2 minimum, 253 sat/kw = 1 sat/vB).</summary>
    public uint MinFeeratePerKw { get; init; } = 253;

    /// <summary>Confirmation target of a sweep without a deadline (<c>Onchain:SweepConfTarget</c>).</summary>
    public uint SweepConfTarget { get; init; } = 36;

    /// <summary>Largest confirmation target ever asked for.</summary>
    public uint MaxConfTarget { get; init; } = 144;

    /// <summary>Blocks kept in hand before a deadline: <c>target = deadline - tip - safety</c>.</summary>
    public uint DeadlineSafetyBlocks { get; init; } = 3;

    /// <summary>Blocks a deadline-bound transaction may stay unconfirmed before it is replaced
    /// (<c>Onchain:RbfIntervalBlocks</c>).</summary>
    public uint RbfIntervalBlocks { get; init; } = 2;

    /// <summary>Replacement fee multiplier, in per-mille of the old fee (1250 = x1.25).</summary>
    public uint RbfFeeMultiplierPerMille { get; init; } = 1250;

    /// <summary>Minimum relay feerate in sat per 1000 vbytes (BIP 125 rule 4 increment).</summary>
    public ulong MinRelayFeePerKvB { get; init; } = 1000;

    /// <summary>Largest share of its input value a sweep or claim may pay, in per-mille (500 = 50 %).</summary>
    public uint SweepMaxFeePerMille { get; init; } = 500;

    /// <summary>Largest share of the revoked value a penalty may pay once its deadline is within
    /// <see cref="SecurityDelay"/> blocks, in per-mille (<c>Onchain:PenaltyMaxFeeFraction</c>, 1000 = 100 %).</summary>
    public uint PenaltyMaxFeePerMille { get; init; } = 1000;

    /// <summary>BOLT 5 <c>security_delay</c>: split a batched penalty and pay up to the penalty cap once this few
    /// blocks remain before a revoked output's deadline (B5-REV-08).</summary>
    public uint SecurityDelay { get; init; } = 18;
}