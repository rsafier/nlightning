// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// A cost-basis lot of the financial book (<c>AccountingLot</c>, NL-602 A3-T4, D-A9, D-A12; migration
/// <c>AddAccountingFinancial</c>): opened by an acquisition (a deposit, income at fair value), an opening balance or a
/// <c>lots import</c>, relieved by disposals (<see cref="AccountingLotReliefEntity"/>) in the method's order (FIFO by
/// <see cref="AcquiredAt"/>, LIFO, HIFO by cost per msat).
/// </summary>
/// <remarks>
/// <see cref="Id"/> is assigned by the repository (one past the highest), not by the database, so a relief can name a
/// lot opened in the same save; the financial projector is the only writer. The open lots are read through the
/// (<see cref="RemainingMsat"/>, <see cref="AcquiredAt"/>) index. Fiat columns are <c>decimal</c>, stored as TEXT on
/// SQLite, so HIFO orders in memory, never in SQL.
/// </remarks>
public class AccountingLotEntity
{
    /// <summary>Lot id (assigned by the repository).</summary>
    public long Id { get; set; }

    /// <summary>When the sats were acquired (UTC ticks).</summary>
    public required DateTimeOffset AcquiredAt { get; set; }

    /// <summary><c>AccountingLotOrigin</c>.</summary>
    public required byte Origin { get; set; }

    /// <summary>The financial entry that opened the lot (with <see cref="SourceAdjustment"/>), null for an imported
    /// lot.</summary>
    public long? SourceLedgerSeq { get; set; }

    public int SourceAdjustment { get; set; }

    /// <summary><c>AccountingLotBucket</c> that holds the lot (the debtor of a debt), or null for a lot of the
    /// node-wide pool of an older book or an imported lot not taken by its opening balance yet (NL-657).</summary>
    public int? Account { get; set; }

    /// <summary>The lot a moved part came from, or null.</summary>
    public long? ParentLotId { get; set; }

    /// <summary>The original acquisition time of a moved part (UTC ticks; FIFO/LIFO and the holding period), or null
    /// for the lot's own <see cref="AcquiredAt"/> (migration <c>AddLotBuckets</c>, NL-657).</summary>
    public DateTimeOffset? HeldSince { get; set; }

    /// <summary><c>AccountingLotBucket</c> a debt (<c>Origin</c> 5) is owed to, or null (migration
    /// <c>AddLotBuckets</c>, NL-657).</summary>
    public int? Lender { get; set; }

    public required long OriginalMsat { get; set; }
    public required long RemainingMsat { get; set; }

    /// <summary>The fiat cost of <see cref="OriginalMsat"/>, or null while unvalued.</summary>
    public decimal? FiatCost { get; set; }

    /// <summary>ISO 4217 code of <see cref="FiatCost"/>.</summary>
    public string? FiatCurrency { get; set; }

    /// <summary>The <c>AccountingPrices</c> row of the cost (foreign key, restrict), or null.</summary>
    public long? PriceId { get; set; }

    /// <summary>True for the cutover's opening lots (D-A9).</summary>
    public required bool BasisEstimated { get; set; }

    /// <summary>The <c>AccountingPeriods</c> id of the close that recorded the lot, or null.</summary>
    public string? ClosedPeriodId { get; set; }

    // Default constructor for EF Core
    internal AccountingLotEntity()
    {
    }
}