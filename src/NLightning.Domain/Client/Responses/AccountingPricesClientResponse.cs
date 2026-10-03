namespace NLightning.Domain.Client.Responses;

using Accounting.Financial;
using Accounting.Prices;

/// <summary>
/// The answer of <c>accounting prices import|list|fetch|replace</c> (<c>ClientCommand.AccountingAdmin</c>, NL-602 A3-T2): the
/// field of the action is set.
/// </summary>
/// <param name="Currency">The currency of the prices.</param>
public sealed record AccountingPricesClientResponse(string Currency)
{
    public IReadOnlyList<AccountingPrice>? Prices { get; init; }

    public AccountingPriceImportResult? Import { get; init; }

    public AccountingPriceFetchResult? Fetch { get; init; }

    public AccountingPriceReplaceResult? Replace { get; init; }
}