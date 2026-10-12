// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// A books projector's cursor (NL-602 A2, plan §6.3): one row per book (key <see cref="Book"/>, migration
/// <c>AddAccountingFinancial</c>; the A2 singleton became the operational book's row) saved with the entries it covers,
/// so the projection is exactly-once.
/// </summary>
public class AccountingCursorEntity
{
    /// <summary><c>AccountingBook</c>.</summary>
    public byte Book { get; set; }

    /// <summary>The ledger sequence of the last projected event (the financial book: of the last operational entry it
    /// projected).</summary>
    public long LastLedgerSeq { get; set; }

    // Default constructor for EF Core
    internal AccountingCursorEntity()
    {
    }
}