namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;

public class SilentPaymentOutputEntity
{
    public TxId TransactionId { get; set; }
    public uint Index { get; set; }
    public required byte[] OutputKey { get; set; }
    public required byte[] Tweak { get; set; }
    public uint? Label { get; set; }
    public long AmountSats { get; set; }
    public uint BlockHeight { get; set; }
    public Hash BlockHash { get; set; }
    public TxId? SpentByTransactionId { get; set; }
    public uint? SpentAtHeight { get; set; }
    public bool Ignored { get; set; }
    public virtual UtxoEntity? Utxo { get; set; }
    internal SilentPaymentOutputEntity() { }
}