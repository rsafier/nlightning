namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// The running balance of one account of a book (<c>AccountingBalances</c>): the sum of every posting to
/// (<see cref="Book"/>, <see cref="Account"/>, <see cref="AccountName"/>), in msat and, for the valued postings, in the
/// book's currency.
/// </summary>
/// <param name="Book">The book.</param>
/// <param name="Account">The account's role.</param>
/// <param name="AccountName">The financial account's name; null in the operational book.</param>
/// <param name="BalanceMsat">The sum of the postings, msat.</param>
/// <param name="FiatAmount">The sum of the valued postings' fiat amounts (0 when none is valued).</param>
public sealed record AccountingAccountBalance(
    AccountingBook Book,
    AccountRole Account,
    string? AccountName,
    long BalanceMsat,
    decimal FiatAmount);