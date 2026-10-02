// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// One line of a books entry (<c>AccountingPosting</c>, NL-602 A2): a debit (positive) or a credit (negative), msat.
/// </summary>
public class AccountingPostingEntity
{
    /// <summary>The entry's ledger sequence.</summary>
    public long LedgerSeq { get; set; }

    /// <summary>The line's position in its entry.</summary>
    public int Index { get; set; }

    /// <summary><c>AccountRole</c>.</summary>
    public int Account { get; set; }

    public long AmountMsat { get; set; }

    /// <summary>The entry's time (UTC ticks), copied here so the period sums per account need no join.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    // Default constructor for EF Core
    internal AccountingPostingEntity()
    {
    }
}