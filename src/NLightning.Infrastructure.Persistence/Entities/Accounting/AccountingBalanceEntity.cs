// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>
/// The running balance of one account of a book (NL-602 A2, plan §10; per book since migration
/// <c>AddAccountingFinancial</c>), updated in the save of the entries that post to it, so a reconcile or a report never
/// sums the history.
/// </summary>
/// <remarks>Key (<see cref="Book"/>, <see cref="Account"/>, <see cref="AccountName"/>): the operational book keys by
/// role with an empty name, the financial book by the posting's role and account name.</remarks>
public class AccountingBalanceEntity
{
    /// <summary><c>AccountingBook</c>; existing rows 0.</summary>
    public byte Book { get; set; }

    /// <summary><c>AccountRole</c>.</summary>
    public int Account { get; set; }

    /// <summary>The financial account's name; empty (never null: it is part of the key) in the operational book.</summary>
    public string AccountName { get; set; } = string.Empty;

    public long BalanceMsat { get; set; }

    /// <summary>The sum of the valued postings' fiat amounts, in the book's currency (0 in the operational book).</summary>
    public decimal FiatAmount { get; set; }

    // Default constructor for EF Core
    internal AccountingBalanceEntity()
    {
    }
}