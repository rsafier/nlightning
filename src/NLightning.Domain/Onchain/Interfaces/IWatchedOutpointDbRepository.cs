namespace NLightning.Domain.Onchain.Interfaces;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Models;

/// <summary>
/// Persistence port for <see cref="WatchedOutpointModel"/> (table <c>WatchedOutpoints</c>). Writes are staged and
/// committed by <c>IUnitOfWork.SaveChangesAsync</c>.
/// </summary>
public interface IWatchedOutpointDbRepository
{
    /// <summary>Stages a new watch.</summary>
    void Add(WatchedOutpointModel watchedOutpoint);

    Task<WatchedOutpointModel?> GetAsync(TxId transactionId, uint outputIndex);

    /// <summary>
    /// The watches to load at startup: every row except those of a channel that is Closed or Stale.
    /// </summary>
    Task<IReadOnlyList<WatchedOutpointModel>> GetActiveAsync();

    /// <summary>Stages the spend of a watched outpoint; does nothing when the outpoint has no row.</summary>
    Task MarkSpentAsync(TxId transactionId, uint outputIndex, TxId spendingTransactionId, uint height,
                        Hash blockHash);

    /// <summary>Stages clearing every spend recorded above <paramref name="height"/> (reorg); returns how many.</summary>
    Task<int> ClearSpendsAboveAsync(uint height);

    /// <summary>
    /// Stages a <see cref="Enums.WatchedOutpointPurpose.FundingOutput"/> watch for the stored channel whose funding
    /// transaction is <paramref name="fundingTransactionId"/>, unless it has one. Returns the new watch, or null when
    /// the channel is unknown, has another funding transaction, or is already watched.
    /// </summary>
    Task<WatchedOutpointModel?> AddFundingOutpointIfMissingAsync(ChannelId channelId, TxId fundingTransactionId);

    /// <summary>
    /// Stages a funding output watch for every stored channel that is past funding_created, not Closed or Stale, and has
    /// none (startup backfill). Returns the new watches.
    /// </summary>
    Task<IReadOnlyList<WatchedOutpointModel>> AddMissingFundingOutpointsAsync();
}