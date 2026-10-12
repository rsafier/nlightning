namespace NLightning.Domain.Accounting.Enums;

/// <summary>
/// How final the fact an accounting event records is. The values are persisted: never renumber them.
/// </summary>
public enum AccountingFinality : byte
{
    /// <summary>Off-chain and irrevocable (a settled HTLC), or a fact that does not depend on the chain.</summary>
    Final = 0,

    /// <summary>On chain, in a block that a reorg may still disconnect.</summary>
    Confirmed = 1,

    /// <summary>On chain and buried deep enough to be treated as permanent (100 blocks, BOLT 5 irrevocable).</summary>
    Irrevocable = 2
}