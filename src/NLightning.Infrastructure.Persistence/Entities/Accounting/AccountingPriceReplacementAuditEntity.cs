namespace NLightning.Infrastructure.Persistence.Entities.Accounting;

/// <summary>The append-only history of operator price corrections (NL-758).</summary>
public sealed class AccountingPriceReplacementAuditEntity
{
    public long Id { get; set; }
    public long PriceId { get; set; }
    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public byte OldSource { get; set; }
    public byte NewSource { get; set; }
    public DateTimeOffset OldFetchedAt { get; set; }
    public DateTimeOffset ReplacedAt { get; set; }
    public string? OperatorSource { get; set; }
    public string? Note { get; set; }
}