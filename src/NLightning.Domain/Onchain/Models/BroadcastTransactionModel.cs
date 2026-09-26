namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// A transaction we broadcast, stored with its raw bytes so it can be rebroadcast after a failed send, a restart, a
/// mempool eviction or a reorg (BOLT 5 plan O0-T1, D4: persist before broadcast).
/// </summary>
/// <remarks>
/// Save it in the same save as the state change that decided the broadcast, then publish it through
/// <see cref="Interfaces.IChainBroadcaster.PublishAsync"/>. While <see cref="State"/> is
/// <see cref="BroadcastState.Pending"/> the chain monitor sends it again after every processed block; it becomes
/// <see cref="BroadcastState.Confirmed"/> when a processed block holds it, and <see cref="BroadcastState.Pending"/>
/// again if that block is disconnected.
/// </remarks>
public sealed class BroadcastTransactionModel
{
    public TxId TransactionId { get; }

    /// <summary>The fully signed transaction.</summary>
    public byte[] RawTransaction { get; }

    public BroadcastPurpose Purpose { get; }

    /// <summary>The channel it belongs to, if any.</summary>
    public ChannelId? ChannelId { get; }

    /// <summary>Its feerate in sat per 1000 weight units, 0 when unknown.</summary>
    public uint FeeratePerKw { get; }

    /// <summary>The transaction it replaces (RBF), if any.</summary>
    public TxId? ReplacesTransactionId { get; }

    /// <summary>The last processed block height when it was first broadcast.</summary>
    public uint FirstBroadcastHeight { get; }

    public BroadcastState State { get; private set; }

    /// <summary>The height of the processed block that holds it, while <see cref="BroadcastState.Confirmed"/>.</summary>
    public uint? ConfirmedHeight { get; private set; }

    /// <summary>The hash of the processed block that holds it, while <see cref="BroadcastState.Confirmed"/>.</summary>
    public Hash? ConfirmedBlockHash { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// For <see cref="BroadcastPurpose.LocalCommitment"/>: the number of our local commitment it is. The signer's
    /// invariant S1 is restored from it at channel registration (NL-297): the per-commitment secret of that commitment
    /// is never released, even after a restart.
    /// </summary>
    public ulong? CommitmentNumber { get; }

    public BroadcastTransactionModel(SignedTransaction transaction, BroadcastPurpose purpose, ChannelId? channelId,
                                     uint firstBroadcastHeight, uint feeratePerKw = 0,
                                     TxId? replacesTransactionId = null, ulong? commitmentNumber = null)
        : this(transaction?.TxId ?? throw new ArgumentNullException(nameof(transaction)), transaction.RawTxBytes,
               purpose, channelId, feeratePerKw, replacesTransactionId, firstBroadcastHeight, BroadcastState.Pending,
               null, null, DateTimeOffset.UtcNow, commitmentNumber)
    {
    }

    private BroadcastTransactionModel(TxId transactionId, byte[] rawTransaction, BroadcastPurpose purpose,
                                      ChannelId? channelId, uint feeratePerKw, TxId? replacesTransactionId,
                                      uint firstBroadcastHeight, BroadcastState state, uint? confirmedHeight,
                                      Hash? confirmedBlockHash, DateTimeOffset createdAt, ulong? commitmentNumber)
    {
        ArgumentNullException.ThrowIfNull(rawTransaction);
        if (rawTransaction.Length == 0)
            throw new ArgumentException("The raw transaction cannot be empty.", nameof(rawTransaction));
        if (state == BroadcastState.Confirmed && (confirmedHeight is null || confirmedBlockHash is null))
            throw new ArgumentException("A confirmed broadcast needs its height and block hash.", nameof(state));

        TransactionId = transactionId;
        RawTransaction = rawTransaction;
        Purpose = purpose;
        ChannelId = channelId;
        FeeratePerKw = feeratePerKw;
        ReplacesTransactionId = replacesTransactionId;
        FirstBroadcastHeight = firstBroadcastHeight;
        State = state;
        ConfirmedHeight = confirmedHeight;
        ConfirmedBlockHash = confirmedBlockHash;
        CreatedAt = createdAt;
        CommitmentNumber = commitmentNumber;
    }

    /// <summary>Rebuilds a stored broadcast (persistence only).</summary>
    public static BroadcastTransactionModel Restore(TxId transactionId, byte[] rawTransaction,
                                                    BroadcastPurpose purpose, ChannelId? channelId,
                                                    uint feeratePerKw, TxId? replacesTransactionId,
                                                    uint firstBroadcastHeight, BroadcastState state,
                                                    uint? confirmedHeight, Hash? confirmedBlockHash,
                                                    DateTimeOffset createdAt, ulong? commitmentNumber = null)
    {
        return new BroadcastTransactionModel(transactionId, rawTransaction, purpose, channelId, feeratePerKw,
                                             replacesTransactionId, firstBroadcastHeight, state, confirmedHeight,
                                             confirmedBlockHash, createdAt, commitmentNumber);
    }

    /// <summary>The transaction as a <see cref="SignedTransaction"/>.</summary>
    public SignedTransaction ToSignedTransaction() => new(TransactionId, RawTransaction);

    /// <summary>A processed block holds it.</summary>
    public void MarkConfirmed(uint height, Hash blockHash)
    {
        if (State is BroadcastState.Replaced or BroadcastState.Abandoned)
            return;

        State = BroadcastState.Confirmed;
        ConfirmedHeight = height;
        ConfirmedBlockHash = blockHash;
    }

    /// <summary>
    /// Given up: it can never confirm (for example our commitment after the peer's commitment spent the funding
    /// output). Not rebroadcast any more; a confirmed one is kept as it is.
    /// </summary>
    public void MarkAbandoned()
    {
        if (State == BroadcastState.Pending)
            State = BroadcastState.Abandoned;
    }

    /// <summary>The block that held it was disconnected: it is pending (and rebroadcast) again.</summary>
    public void MarkUnconfirmed()
    {
        if (State != BroadcastState.Confirmed)
            return;

        State = BroadcastState.Pending;
        ConfirmedHeight = null;
        ConfirmedBlockHash = null;
    }
}