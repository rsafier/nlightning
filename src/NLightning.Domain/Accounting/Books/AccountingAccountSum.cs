namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// The postings of one account of a book summed over a window (NL-602 A3-T6: the financial reports), in msat and in one
/// fiat currency.
/// </summary>
/// <param name="Book">The book.</param>
/// <param name="Account">The account's role.</param>
/// <param name="AccountName">The financial account's name; null in the operational book.</param>
/// <param name="AmountMsat">The sum of the postings, msat.</param>
/// <param name="FiatAmount">The sum of the fiat amounts of the postings valued in the currency asked for (0 when none).
/// </param>
/// <param name="UnvaluedPostings">How many postings with an msat amount have no value in that currency.</param>
public sealed record AccountingAccountSum(
    AccountingBook Book,
    AccountRole Account,
    string? AccountName,
    long AmountMsat,
    decimal FiatAmount,
    int UnvaluedPostings);