namespace NLightning.Domain.Bitcoin.Events;

using Channels.ValueObjects;
using ValueObjects;

/// <summary>
/// A transaction bitcoind accepted into its mempool spends a watched outpoint of a channel, or an output of a
/// transaction reported this way before (BOLT 5 plan O8, NL-098).
/// </summary>
/// <remarks>
/// Never a confirmation: the transaction may be replaced, evicted or never mined. Use it only for what is safe on an
/// unconfirmed transaction: reading a preimage (a preimage stays valid whatever happens to the transaction) and
/// preparing a reaction that the confirmed path completes or abandons (a penalty for a revoked commitment).
/// </remarks>
public sealed class MempoolSpendEventArgs : EventArgs
{
    /// <summary>The channel whose outpoint (or whose earlier reported transaction's output) is spent.</summary>
    public ChannelId ChannelId { get; }

    /// <summary>The unconfirmed spending transaction.</summary>
    public SignedTransaction SpendingTransaction { get; }

    /// <summary>The transaction of the spent outpoint.</summary>
    public TxId SpentTransactionId { get; }

    /// <summary>The output index of the spent outpoint.</summary>
    public uint SpentOutputIndex { get; }

    /// <summary>
    /// True when the spent output belongs to an unconfirmed transaction reported before (e.g. an HTLC transaction
    /// spending a commitment that is itself still in the mempool), not to a watched outpoint.
    /// </summary>
    public bool SpendsUnconfirmedParent { get; }

    public MempoolSpendEventArgs(ChannelId channelId, SignedTransaction spendingTransaction, TxId spentTransactionId,
                                 uint spentOutputIndex, bool spendsUnconfirmedParent)
    {
        ArgumentNullException.ThrowIfNull(spendingTransaction);
        ChannelId = channelId;
        SpendingTransaction = spendingTransaction;
        SpentTransactionId = spentTransactionId;
        SpentOutputIndex = spentOutputIndex;
        SpendsUnconfirmedParent = spendsUnconfirmedParent;
    }
}