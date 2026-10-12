namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Bitcoin.ValueObjects;

/// <summary>An operator label survives confirmation, reorgs and delayed history discovery.</summary>
public sealed class WalletTransactionLabelEntity
{
    public TxId TransactionId { get; set; }
    public string Label { get; set; } = string.Empty;
}