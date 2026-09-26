namespace NLightning.Domain.Onchain.Interfaces;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Models;

/// <summary>
/// Persistence port for <see cref="BroadcastTransactionModel"/> (table <c>BroadcastTransactions</c>). Writes are staged
/// and committed by <c>IUnitOfWork.SaveChangesAsync</c>.
/// </summary>
public interface IBroadcastTransactionDbRepository
{
    /// <summary>Stages a new broadcast (in the same save as the state change that decided it).</summary>
    void Add(BroadcastTransactionModel transaction);

    Task<BroadcastTransactionModel?> GetByTransactionIdAsync(TxId transactionId);

    /// <summary>Every broadcast of the channel, oldest first.</summary>
    Task<IReadOnlyList<BroadcastTransactionModel>> GetByChannelIdAsync(ChannelId channelId);

    /// <summary>
    /// Stages giving up a pending broadcast (it conflicts with a transaction on chain); does nothing for a missing,
    /// confirmed, replaced or abandoned one. Returns true when it changed the row.
    /// </summary>
    Task<bool> MarkAbandonedAsync(TxId transactionId);

    /// <summary>
    /// Stages recording that a pending broadcast was replaced (RBF, BOLT 5 plan O6-T1) by a transaction whose row names
    /// it in <see cref="BroadcastTransactionModel.ReplacesTransactionId"/>; it is no longer rebroadcast. Does nothing for
    /// a missing or non-pending one. Returns true when it changed the row.
    /// </summary>
    Task<bool> MarkReplacedAsync(TxId transactionId);

    /// <summary>
    /// Stages making an abandoned or replaced broadcast pending again (BOLT 5 plan O6-T3: the transaction that
    /// superseded it was reorged out, so it is sent again after every block). Does nothing for a missing, pending or
    /// confirmed one. Returns true when it changed the row.
    /// </summary>
    Task<bool> MarkPendingAsync(TxId transactionId);

    /// <summary>Every broadcast that is still pending (the rebroadcast set).</summary>
    Task<IReadOnlyList<BroadcastTransactionModel>> GetPendingAsync();

    /// <summary>Stages the confirmation of a broadcast; does nothing when it has no row.</summary>
    Task MarkConfirmedAsync(TxId transactionId, uint height, Hash blockHash);

    /// <summary>
    /// Stages turning every broadcast confirmed above <paramref name="height"/> back to pending (reorg); returns how
    /// many.
    /// </summary>
    Task<int> UnconfirmAboveAsync(uint height);
}