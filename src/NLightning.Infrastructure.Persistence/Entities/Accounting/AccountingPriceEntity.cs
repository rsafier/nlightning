// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// A stored BTC price (<c>AccountingPrice</c>, NL-602 A3-T2, D-A11; migration <c>AddAccountingFinancial</c>): written
/// by the back-valuation job, the price file and <c>prices import</c>, read when a posting or a lot is valued. Unique per
/// (<see cref="Currency"/>, <see cref="Time"/>); never changed once stored, because valued postings and lots reference
/// it by <see cref="Id"/>.
/// </summary>
public class AccountingPriceEntity
{
    /// <summary>Storage id (identity).</summary>
    public long Id { get; set; }

    /// <summary>ISO 4217 code, three letters.</summary>
    public required string Currency { get; set; }

    /// <summary>The time the price is for (UTC ticks); the nearest-at-or-before lookup reads it through the unique
    /// (<see cref="Currency"/>, <see cref="Time"/>) index.</summary>
    public required DateTimeOffset Time { get; set; }

    /// <summary>The price of 1 BTC (decimal, 8 places).</summary>
    public required decimal Price { get; set; }

    /// <summary><c>AccountingPriceSource</c>.</summary>
    public required byte Source { get; set; }

    /// <summary>When we stored it (UTC ticks).</summary>
    public required DateTimeOffset FetchedAt { get; set; }

    // Default constructor for EF Core
    internal AccountingPriceEntity()
    {
    }
}