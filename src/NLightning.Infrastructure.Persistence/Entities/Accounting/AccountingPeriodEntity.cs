// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// An accounting period and its close (<c>AccountingPeriod</c>, NL-602 A3-T5, D-A8, D-A13; migration
/// <c>AddAccountingFinancial</c>), keyed by <see cref="PeriodId"/> (<c>YYYY-MM</c> or <c>YYYY-MM-DD..YYYY-MM-DD</c>).
/// A closed period is never rewritten: its entries, lots and reliefs carry its id (<c>ClosedPeriodId</c>) and
/// <c>verify</c> recomputes <see cref="Digest"/> from them and checks <see cref="Signature"/> against the node id and
/// <see cref="ChainHash"/> against the feed.
/// </summary>
public class AccountingPeriodEntity
{
    public required string PeriodId { get; set; }

    /// <summary>The first instant of the period (UTC ticks).</summary>
    public required DateTimeOffset Start { get; set; }

    /// <summary>The first instant after it (UTC ticks, exclusive).</summary>
    public required DateTimeOffset End { get; set; }

    /// <summary><c>AccountingPeriodState</c>.</summary>
    public required byte State { get; set; }

    /// <summary>When it was closed (UTC ticks).</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The feed's last ledger sequence the close covers.</summary>
    public required long LastLedgerSeq { get; set; }

    /// <summary>The feed's 32-byte chain hash at <see cref="LastLedgerSeq"/>.</summary>
    public byte[]? ChainHash { get; set; }

    /// <summary>The 32-byte close digest (D-A13).</summary>
    public byte[]? Digest { get; set; }

    /// <summary>The node key's signature of <see cref="Digest"/> (<c>ILightningSigner.SignNodeMessage</c>).</summary>
    public byte[]? Signature { get; set; }

    /// <summary>Closed with <c>--force</c>.</summary>
    public required bool Forced { get; set; }

    /// <summary>The financial book's state at the close (JSON, written by the closing lane), so a financial rebuild
    /// starts from it (D-A8).</summary>
    public string? ClosingState { get; set; }

    // Default constructor for EF Core
    internal AccountingPeriodEntity()
    {
    }
}