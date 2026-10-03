// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// The part of a lot one disposal used (<c>AccountingLotRelief</c>, NL-602 A3-T4, D-A12; migration
/// <c>AddAccountingFinancial</c>): realized gain = <see cref="Proceeds"/> − <see cref="FiatCostRelieved"/>, pending
/// valuation while either is null. Deleted with its lot (cascade).
/// </summary>
public class AccountingLotReliefEntity
{
    /// <summary>Storage id (identity).</summary>
    public long Id { get; set; }

    /// <summary>The lot (foreign key, cascade).</summary>
    public required long LotId { get; set; }

    /// <summary>The disposing financial entry's ledger sequence (with <see cref="Adjustment"/>; no foreign key: a
    /// financial rebuild of the open period deletes and rewrites both).</summary>
    public required long LedgerSeq { get; set; }

    public int Adjustment { get; set; }

    /// <summary>When the disposal happened (UTC ticks): realized gains by period read it.</summary>
    public required DateTimeOffset RelievedAt { get; set; }

    public required long Msat { get; set; }

    public decimal? FiatCostRelieved { get; set; }
    public decimal? Proceeds { get; set; }

    /// <summary>The <c>AccountingPeriods</c> id of the close that holds the relief, or null.</summary>
    public string? ClosedPeriodId { get; set; }

    /// <summary><c>AccountingLotReliefKind</c>: 0 a disposal, 1 a move to another bucket, 2 a debt's settlement
    /// (migration <c>AddLotBuckets</c>, NL-657; the reliefs written before are disposals).</summary>
    public byte Kind { get; set; }

    // Default constructor for EF Core
    internal AccountingLotReliefEntity()
    {
    }
}