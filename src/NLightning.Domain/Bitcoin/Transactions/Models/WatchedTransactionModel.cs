namespace NLightning.Domain.Bitcoin.Transactions.Models;

using Channels.ValueObjects;
using ValueObjects;

public sealed class WatchedTransactionModel
{
    public ChannelId ChannelId { get; }
    public TxId TransactionId { get; }
    public uint RequiredDepth { get; }
    public uint? FirstSeenAtHeight { get; private set; }
    /// <summary>
    /// Position of the transaction within its block (0 is the coinbase), as used by the BOLT 7 short_channel_id.
    /// </summary>
    public uint? TransactionIndex { get; private set; }
    public bool IsCompleted { get; private set; }

    public WatchedTransactionModel(ChannelId channelId, TxId transactionId, uint requiredDepth)
    {
        ChannelId = channelId;
        TransactionId = transactionId;
        RequiredDepth = requiredDepth;
    }

    public void SetHeightAndIndex(uint height, uint txIndex)
    {
        // short_channel_id stores the transaction index in 3 bytes
        ArgumentOutOfRangeException.ThrowIfGreaterThan(txIndex, ShortChannelId.MaxThreeByteValue);

        if (FirstSeenAtHeight.HasValue)
            throw new InvalidOperationException($"{nameof(FirstSeenAtHeight)} is already set.");

        if (TransactionIndex.HasValue)
            throw new InvalidOperationException($"{nameof(TransactionIndex)} is already set.");

        FirstSeenAtHeight = height;
        TransactionIndex = txIndex;
    }

    public void MarkAsCompleted()
    {
        if (IsCompleted)
            throw new InvalidOperationException("Transaction is already marked as completed.");

        IsCompleted = true;
    }
}