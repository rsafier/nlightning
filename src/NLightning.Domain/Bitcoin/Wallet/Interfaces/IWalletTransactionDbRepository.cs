namespace NLightning.Domain.Bitcoin.Wallet.Interfaces;

using Models;
using ValueObjects;

/// <summary>
/// The on-chain wallet's durable transaction history (NL-1187, table <c>WalletTransactions</c>). Writes are staged on
/// the unit of work.
/// </summary>
public interface IWalletTransactionDbRepository
{
    /// <summary>
    /// Stages a transaction a block confirmed. A transaction already stored takes the new block and keeps the union of
    /// the wallet outputs and inputs both descriptions found (a replayed block, whose spent outputs the wallet no
    /// longer holds, never loses an input).
    /// </summary>
    Task StageConfirmedAsync(WalletTransactionRecord record);

    /// <summary>Stages every transaction confirmed above <paramref name="height"/> as unconfirmed (a reorg); returns
    /// how many.</summary>
    Task<int> UnconfirmAboveAsync(uint height);

    /// <summary>
    /// The stored transactions a reorg made unconfirmed (no block holds them now); the chain monitor confirms them
    /// again when a block of the active chain holds them and removes them once a block confirms a conflicting spend.
    /// </summary>
    Task<IReadOnlyList<WalletTransactionRecord>> GetUnconfirmedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stages the removal of a stored transaction a confirmed conflicting spend made invalid (it can never confirm,
    /// as btcwallet drops a conflicted unmined transaction).
    /// </summary>
    Task StageRemoveAsync(TxId txId);

    /// <summary>
    /// The confirmation height of every stored transaction (null for one a reorg unconfirmed): the durable history's
    /// verdict, which a stale height from another source never overrides.
    /// </summary>
    Task<IReadOnlyDictionary<TxId, uint?>> GetHeightsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The stored transactions confirmed from <paramref name="startHeight"/> to <paramref name="endHeight"/>
    /// (inclusive), and the unconfirmed ones when <paramref name="includeUnconfirmed"/>.
    /// </summary>
    Task<IReadOnlyList<WalletTransactionRecord>> GetHistoryAsync(uint startHeight, uint endHeight,
                                                                 bool includeUnconfirmed,
                                                                 CancellationToken cancellationToken);
}

/// <summary>The history of a unit of work that keeps none (test doubles): writes are dropped, reads are empty.</summary>
public sealed class NullWalletTransactionDbRepository : IWalletTransactionDbRepository
{
    /// <summary>The instance.</summary>
    public static NullWalletTransactionDbRepository Instance { get; } = new();

    private NullWalletTransactionDbRepository()
    {
    }

    /// <inheritdoc />
    public Task StageConfirmedAsync(WalletTransactionRecord record) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<int> UnconfirmAboveAsync(uint height) => Task.FromResult(0);

    /// <inheritdoc />
    public Task<IReadOnlyList<WalletTransactionRecord>> GetUnconfirmedAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WalletTransactionRecord>>([]);

    /// <inheritdoc />
    public Task StageRemoveAsync(TxId txId) => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<TxId, uint?>> GetHeightsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<TxId, uint?>>(new Dictionary<TxId, uint?>());

    /// <inheritdoc />
    public Task<IReadOnlyList<WalletTransactionRecord>> GetHistoryAsync(uint startHeight, uint endHeight,
                                                                        bool includeUnconfirmed,
                                                                        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WalletTransactionRecord>>([]);
}