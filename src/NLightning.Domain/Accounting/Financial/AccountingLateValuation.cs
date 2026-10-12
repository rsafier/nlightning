namespace NLightning.Domain.Accounting.Financial;

using Books;

/// <summary>
/// A price found for a posting of a closed period (NL-602 A3-T2): the posting is never filled (D-A8); the adjustment
/// rule (A3-T5) posts the value in the open period instead.
/// </summary>
/// <param name="Posting">The unvalued posting of the closed period (its key, time, account, msat and period).</param>
/// <param name="Price">The stored price D-A11's rule picks for it (saved: its id is set).</param>
/// <param name="FiatAmount">The posting's value at that price (<c>AccountingValuation.FiatValue</c>).</param>
public sealed record AccountingLateValuation(AccountingUnvaluedPosting Posting, AccountingPrice Price,
                                             decimal FiatAmount);