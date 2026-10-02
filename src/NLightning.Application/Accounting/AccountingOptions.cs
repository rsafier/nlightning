namespace NLightning.Application.Accounting;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;

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

    /// <summary>
    /// Account name overrides by role (plan §6.1; e.g. <c>Accounting:AccountNames:Routing</c> =
    /// <c>income:routing</c>); roles not named keep their default.
    /// </summary>
    public Dictionary<AccountRole, string> AccountNames { get; set; } = [];

    /// <summary>The account names in effect.</summary>
    public AccountNames GetAccountNames() => new(AccountNames);

    /// <summary>Whether the books run (<see cref="Enabled"/> unset = on).</summary>
    public bool AreBooksEnabled => Enabled ?? true;

    /// <summary>
    /// Which books are kept (<c>Accounting:Profile</c>, plan §6.2, §7, D-A5): <see cref="AccountingProfile.Operational"/>
    /// (the default) or <see cref="AccountingProfile.Financial"/>, which adds the financial book next to the
    /// operational one (D-A7). The classification rules and overrides are stored and managed either way (NL-602
    /// A3-T3).
    /// </summary>
    public AccountingProfile Profile { get; set; } = AccountingProfile.Operational;

    /// <summary>
    /// The financial chart's name overrides by account (A3-T3; e.g. <c>Accounting:FinancialAccountNames:Sales</c> =
    /// <c>income:consulting</c>); accounts not named keep their default, invalid names are ignored (and reported).
    /// </summary>
    public Dictionary<FinancialAccount, string> FinancialAccountNames { get; set; } = [];

    /// <summary>The financial chart in effect (its assets, opening balances and transfers follow
    /// <see cref="GetAccountNames"/>).</summary>
    public FinancialChart GetFinancialChart() => new(GetAccountNames(), FinancialAccountNames);
}