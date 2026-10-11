namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// A period and what its close holds (A3-T5): <c>close</c>, <c>close list</c> and <c>close show</c>.
/// </summary>
/// <param name="Period">The stored period.</param>
/// <param name="ClosingState">Its closing state, or null when open (or unreadable).</param>
public sealed record AccountingCloseReport(AccountingPeriod Period, AccountingClosingState? ClosingState)
{
    /// <summary>The node id whose key signed the digest (33 bytes), when known.</summary>
    public byte[]? NodeId { get; init; }

    /// <summary>The financial entries the close covers (set by a close).</summary>
    public long? EntryCount { get; init; }

    /// <summary>The lot reliefs the close covers (set by a close).</summary>
    public long? ReliefCount { get; init; }

    /// <summary>The lots open at the period's end (set by a close).</summary>
    public long? OpenLotCount { get; init; }

    /// <summary>The postings of the period without a fiat value at the close (set by a close; non-zero only when
    /// forced).</summary>
    public int? UnvaluedPostings { get; init; }

    /// <summary>The unclassified entries of the period at the close (set by a close).</summary>
    public int? UnclassifiedEntries { get; init; }
}