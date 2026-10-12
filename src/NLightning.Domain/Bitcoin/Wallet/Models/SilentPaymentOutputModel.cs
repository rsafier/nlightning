namespace NLightning.Domain.Bitcoin.Wallet.Models;

using Crypto.ValueObjects;
using ValueObjects;

/// <summary>A discovered output retained after spending so disconnected spends can be restored.</summary>
public sealed record SilentPaymentOutputModel(TxId TransactionId, uint Index, byte[] OutputKey, byte[] Tweak,
    uint? Label, long AmountSats, uint BlockHeight, Hash BlockHash, TxId? SpentByTransactionId = null,
    bool Ignored = false, uint? SpentAtHeight = null);