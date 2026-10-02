namespace NLightning.Domain.Client.Requests;

using Accounting.Prices;

/// <summary>
/// The arguments of <c>accounting prices import|list|fetch</c> (<c>ClientCommand.AccountingAdmin</c>, NL-602 A3-T2).
/// </summary>
public sealed class AccountingPricesClientRequest
{
    /// <summary>The prices' currency (import, list), or null for the configured one.</summary>
    public string? Currency { get; init; }

    /// <summary>The prices to store (import).</summary>
    public IReadOnlyList<AccountingPricePoint> Points { get; init; } = [];

    /// <summary>The start of the range (list: inclusive; fetch: required, its hour).</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>The end of the range, exclusive (fetch: now when unset).</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>The most prices listed.</summary>
    public int Limit { get; init; } = 100;
}