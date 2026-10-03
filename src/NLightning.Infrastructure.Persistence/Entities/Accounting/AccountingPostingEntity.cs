// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// One line of a books entry (<c>AccountingPosting</c>, NL-602 A2): a debit (positive) or a credit (negative), msat; in
/// the financial book (migration <c>AddAccountingFinancial</c>, A3-T0) also its fiat value.
/// </summary>
/// <remarks>Key (<see cref="Book"/>, <see cref="LedgerSeq"/>, <see cref="Adjustment"/>, <see cref="Index"/>); the first
/// three are the entry's.</remarks>
public class AccountingPostingEntity
{
    /// <summary>The entry's book (<c>AccountingBook</c>); existing rows 0.</summary>
    public byte Book { get; set; }

    /// <summary>The entry's ledger sequence.</summary>
    public long LedgerSeq { get; set; }

    /// <summary>The entry's adjustment number.</summary>
    public int Adjustment { get; set; }

    /// <summary>The line's position in its entry.</summary>
    public int Index { get; set; }

    /// <summary><c>AccountRole</c>: in the financial book the operational role the line derives from.</summary>
    public int Account { get; set; }

    /// <summary>The financial account's name (always set in the financial book); null in the operational book, whose
    /// names come from the role.</summary>
    public string? AccountName { get; set; }

    public long AmountMsat { get; set; }

    /// <summary>The entry's time (UTC ticks), copied here so the period sums per account need no join.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>The line's value in <see cref="FiatCurrency"/> (decimal, 8 places; A3-T2), or null while unvalued.</summary>
    public decimal? FiatAmount { get; set; }

    /// <summary>The ISO 4217 code of <see cref="FiatAmount"/>.</summary>
    public string? FiatCurrency { get; set; }

    /// <summary>The <c>AccountingPrices</c> row the line was valued at (foreign key, restrict).</summary>
    public long? PriceId { get; set; }

    // Default constructor for EF Core
    internal AccountingPostingEntity()
    {
    }
}