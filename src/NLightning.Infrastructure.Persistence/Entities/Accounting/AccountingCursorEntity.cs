// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// The books' projector cursor (NL-602 A2, plan §6.3): a single row (<see cref="Id"/> = <see cref="SingletonId"/>)
/// saved with the entries it covers, so the projection is exactly-once.
/// </summary>
public class AccountingCursorEntity
{
    /// <summary>The only row's id.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; }

    /// <summary>The ledger sequence of the last projected event.</summary>
    public long LastLedgerSeq { get; set; }

    // Default constructor for EF Core
    internal AccountingCursorEntity()
    {
    }
}