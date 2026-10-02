// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// The running balance of one account of the books (NL-602 A2, plan §10), updated in the save of the entries that post
/// to it, so a reconcile never sums the history.
/// </summary>
public class AccountingBalanceEntity
{
    /// <summary><c>AccountRole</c>.</summary>
    public int Account { get; set; }

    public long BalanceMsat { get; set; }

    // Default constructor for EF Core
    internal AccountingBalanceEntity()
    {
    }
}