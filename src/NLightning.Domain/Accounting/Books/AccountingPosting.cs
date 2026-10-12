namespace NLightning.Domain.Accounting.Books;

/// <summary>One line of an entry: a debit (positive) or credit (negative) to an account, in msat.</summary>
/// <remarks>
/// In the financial book (D-A7) <see cref="Account"/> is the operational role the line derives from (or the role a
/// financial projector chooses for a line of its own, such as a realized gain) and <see cref="AccountName"/> is the
/// financial account, always set there; the operational book derives its names from the role and leaves it null.
/// </remarks>
public sealed record AccountingPosting(AccountRole Account, long AmountMsat)
{
    /// <summary>The financial account's name (the financial chart's or a rule's or an override's target); null in
    /// the operational book.</summary>
    public string? AccountName { get; init; }

    /// <summary>The line's value in <see cref="FiatCurrency"/> at the time of the entry (A3-T2), or null while
    /// unvalued.</summary>
    public decimal? FiatAmount { get; init; }

    /// <summary>The ISO 4217 code of <see cref="FiatAmount"/>.</summary>
    public string? FiatCurrency { get; init; }

    /// <summary>The <c>AccountingPrices</c> row the line was valued at.</summary>
    public long? PriceId { get; init; }
}