namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// An outpoint the chain monitor watches for a spend (BOLT 5 plan O0-T2, D1: outpoints, not txids).
/// </summary>
/// <remarks>
/// The spend is recorded for the block that holds it and cleared again when that block is disconnected (reorg), so
/// the row always describes the active chain. The outpoint stays watched after a spend: a replayed or reorged block
/// raises the spend again, so listeners must be idempotent.
/// </remarks>
public sealed class WatchedOutpointModel
{
    /// <summary>The transaction holding the watched output.</summary>
    public TxId TransactionId { get; }

    /// <summary>The index of the watched output in <see cref="TransactionId"/>.</summary>
    public uint OutputIndex { get; }

    /// <summary>The channel the output belongs to.</summary>
    public ChannelId ChannelId { get; }

    /// <summary>Why it is watched.</summary>
    public WatchedOutpointPurpose Purpose { get; }

    /// <summary>The transaction that spent it in the active chain, if any.</summary>
    public TxId? SpentByTransactionId { get; private set; }

    /// <summary>The height of the block holding <see cref="SpentByTransactionId"/>.</summary>
    public uint? SpentAtHeight { get; private set; }

    /// <summary>The hash of the block holding <see cref="SpentByTransactionId"/>.</summary>
    public Hash? SpentBlockHash { get; private set; }

    /// <summary>When the watch was created.</summary>
    public DateTimeOffset CreatedAt { get; }

    public WatchedOutpointModel(TxId transactionId, uint outputIndex, ChannelId channelId,
                                WatchedOutpointPurpose purpose, DateTimeOffset? createdAt = null)
    {
        TransactionId = transactionId;
        OutputIndex = outputIndex;
        ChannelId = channelId;
        Purpose = purpose;
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow;
    }

    /// <summary>True when a spend in the active chain is recorded.</summary>
    public bool IsSpent => SpentAtHeight.HasValue;

    /// <summary>Records the spend seen in a processed block.</summary>
    public void MarkSpent(TxId spendingTransactionId, uint height, Hash blockHash)
    {
        SpentByTransactionId = spendingTransactionId;
        SpentAtHeight = height;
        SpentBlockHash = blockHash;
    }

    /// <summary>Forgets the spend (its block was disconnected).</summary>
    public void ClearSpend()
    {
        SpentByTransactionId = null;
        SpentAtHeight = null;
        SpentBlockHash = null;
    }
}