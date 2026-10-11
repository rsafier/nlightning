namespace NLightning.Domain.Accounting.Financial;

/// <summary>An immutable operator correction, committed together with its price and financial consequences.</summary>
public sealed record AccountingPriceReplacementAudit(long Id, long PriceId, decimal OldPrice, decimal NewPrice,
    AccountingPriceSource OldSource, AccountingPriceSource NewSource, DateTimeOffset OldFetchedAt,
    DateTimeOffset ReplacedAt, string? OperatorSource, string? Note);