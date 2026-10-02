namespace NLightning.Application.Accounting;

/// <summary>
/// Options of the accounting feed and books (section <see cref="SectionName"/>, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §7, NL-602). The feed and its sealer are always on (D-A5); only the books
/// follow <see cref="Enabled"/>.
/// </summary>
public sealed class AccountingOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Accounting";

    /// <summary>The default <see cref="SealInterval"/>.</summary>
    public static readonly TimeSpan DefaultSealInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether the books (projector, reports, valuation) run; unset means on. The feed and the sealer ignore it: the
    /// books cannot be rebuilt for a period the feed did not record.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>How often the sealer seals the events committed since its last round (5 s; zero or less = the
    /// default). A writer may ask for an earlier round (<c>IAccountingEventSealer.Nudge</c>).</summary>
    public TimeSpan SealInterval { get; set; } = DefaultSealInterval;

    /// <summary>How many events one sealer save handles (500).</summary>
    public int SealBatchSize { get; set; } = 500;

    /// <summary>How often the books take a balance snapshot to reconcile against (1 h; used by the books).</summary>
    public TimeSpan SnapshotInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Whether the books run (<see cref="Enabled"/> unset = on).</summary>
    public bool AreBooksEnabled => Enabled ?? true;
}