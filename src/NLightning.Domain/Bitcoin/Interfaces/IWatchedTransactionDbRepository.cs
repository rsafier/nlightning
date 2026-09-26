namespace NLightning.Domain.Bitcoin.Interfaces;

using Transactions.Models;
using ValueObjects;

public interface IWatchedTransactionDbRepository
{
    void Add(WatchedTransactionModel watchedTransactionModel);
    void Update(WatchedTransactionModel watchedTransactionModel);
    Task<IEnumerable<WatchedTransactionModel>> GetAllPendingAsync();
    Task<WatchedTransactionModel?> GetByTransactionIdAsync(TxId transactionId);

    /// <summary>
    /// Stages forgetting the first-seen height and index of every watch that is not completed and was first seen above
    /// <paramref name="height"/> (its block was disconnected, reorg), so the new branch finds it again. Returns how many.
    /// </summary>
    Task<int> ResetPendingFirstSeenAboveAsync(uint height);

    /// <summary>The completed watches first seen above <paramref name="height"/> (their block was disconnected).</summary>
    Task<IReadOnlyList<WatchedTransactionModel>> GetCompletedFirstSeenAboveAsync(uint height);
}