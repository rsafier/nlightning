namespace NLightning.Application.Tests.Onchain;

using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds.Interfaces;
using Domain.LiquidityAds.Models;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx.Interfaces;

/// <summary>
/// An in-memory database for the BOLT 5 wiring tests: the on-chain resolution, watched outpoint, broadcast, channel and
/// revocation log repositories behind a Moq <see cref="IUnitOfWork"/>. Writes apply at once; <see cref="Saves"/>
/// records, for every <c>SaveChangesAsync</c>, what was written since the previous one, so a test can prove that
/// related rows went in one save.
/// </summary>
internal sealed class OnchainTestStore
{
    private readonly List<string> _pending = [];
    private readonly List<Action> _undo = [];
    private readonly List<AccountingEventModel> _stagedEvents = [];

    public Dictionary<ChannelId, ChannelCloseModel> Closes { get; } = [];
    public Dictionary<(TxId, uint), OutputResolutionModel> Outputs { get; } = [];
    public Dictionary<(TxId, uint), WatchedOutpointModel> Watches { get; } = [];
    public List<BroadcastTransactionModel> Broadcasts { get; } = [];
    public Dictionary<TxId, WatchedTransactionModel> TransactionWatches { get; } = [];
    public List<ChannelState> PersistedChannelStates { get; } = [];
    public List<ChannelId> DeletedRevocationLogs { get; } = [];
    public List<ChannelId> DeletedInteractiveTxSessions { get; } = [];
    public Dictionary<(ChannelId, ulong), RevokedCommitmentModel> RevocationLog { get; } = [];

    /// <summary>The origins stored per outgoing HTLC (<c>ChannelStateDbRepository.GetHtlcOriginAsync</c>).</summary>
    public Dictionary<(ChannelId ChannelId, HtlcDirection Direction, ulong HtlcId), HtlcOrigin> Origins { get; } = [];

    /// <summary>The forward circuits (<c>ForwardCircuitDbRepository.GetByIncomingAsync</c>).</summary>
    public Dictionary<(ChannelId IncomingChannelId, ulong IncomingHtlcId), ForwardCircuitModel> Circuits { get; } = [];

    /// <summary>Our invoices by payment hash (<c>InvoiceDbRepository.GetByPaymentHashAsync</c>, NL-688).</summary>
    public Dictionary<Hash, InvoiceModel> Invoices { get; } = [];

    /// <summary>The liquidity purchases (<c>LiquidityPurchaseDbRepository</c>, NL-771).</summary>
    public List<LiquidityPurchaseModel> Purchases { get; } = [];

    /// <summary>The first commitment number the revocation log covers (<c>GetLogStartAsync</c>).</summary>
    public ulong RevocationLogStart { get; set; }

    /// <summary>What <c>ChannelDbRepository.GetByIdAsync</c> returns (the database's copy of a channel).</summary>
    public Func<ChannelId, ChannelModel?>? LoadChannel { get; set; }

    /// <summary>Spends recorded on watches (<c>MarkSpentAsync</c>): outpoint → (spending txid, height).</summary>
    public Dictionary<(TxId, uint), (TxId SpendingTxId, uint Height)> WatchSpends { get; } = [];

    /// <summary>Per save, the writes it committed (e.g. "close", "output 1", "watch 1", "channel OnchainResolving").
    /// </summary>
    public List<IReadOnlyList<string>> Saves { get; } = [];

    /// <summary>
    /// The accounting events committed by the saves (NL-602), each with the index in <see cref="Saves"/> of the save
    /// that committed it (so a test proves an event went in the save of the state change it records).
    /// </summary>
    public List<(AccountingEventModel Event, int Save)> AccountingEvents { get; } = [];

    /// <summary>Called after every successful save (to order saves against other calls).</summary>
    public Action? OnSave { get; set; }

    /// <summary>Set to make the next save throw.</summary>
    public Exception? FailNextSave { get; set; }

    public Mock<IUnitOfWork> CreateUnitOfWork()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(new ResolutionRepository(this));
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(CreateWatchedOutpoints().Object);
        unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository).Returns(CreateWatchedTransactions().Object);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(CreateBroadcasts().Object);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(CreateChannels().Object);
        unitOfWork.SetupGet(u => u.RevokedCommitmentDbRepository).Returns(CreateRevocationLog().Object);
        unitOfWork.SetupGet(u => u.InteractiveTxSessionDbRepository).Returns(CreateInteractiveTxSessions().Object);
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(CreateChannelState().Object);
        unitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(CreateCircuits().Object);
        unitOfWork.SetupGet(u => u.InvoiceDbRepository).Returns(CreateInvoices().Object);
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(new AccountingRepository(this));
        unitOfWork.SetupGet(u => u.LiquidityPurchaseDbRepository).Returns(CreatePurchases().Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            if (FailNextSave is { } failure)
            {
                // Nothing of a failed save is kept
                FailNextSave = null;
                _pending.Clear();
                _stagedEvents.Clear();
                for (var i = _undo.Count - 1; i >= 0; i--)
                    _undo[i]();
                _undo.Clear();
                throw failure;
            }

            Saves.Add(_pending.ToList());
            _pending.Clear();
            foreach (var staged in _stagedEvents)
                AccountingEvents.Add((staged, Saves.Count - 1));
            _stagedEvents.Clear();
            _undo.Clear();
            OnSave?.Invoke();
            return Task.CompletedTask;
        });
        return unitOfWork;
    }

    private Mock<IWatchedOutpointDbRepository> CreateWatchedOutpoints()
    {
        var repository = new Mock<IWatchedOutpointDbRepository>();
        repository.Setup(r => r.Add(It.IsAny<WatchedOutpointModel>()))
                  .Callback<WatchedOutpointModel>(w =>
                   {
                       Watches[(w.TransactionId, w.OutputIndex)] = w;
                       _undo.Add(() => Watches.Remove((w.TransactionId, w.OutputIndex)));
                       _pending.Add($"watch {w.OutputIndex}");
                   });
        repository.Setup(r => r.GetAsync(It.IsAny<TxId>(), It.IsAny<uint>()))
                  .ReturnsAsync((TxId txId, uint index) => Watches.GetValueOrDefault((txId, index)));
        repository.Setup(r => r.MarkSpentAsync(It.IsAny<TxId>(), It.IsAny<uint>(), It.IsAny<TxId>(), It.IsAny<uint>(),
                                               It.IsAny<Hash>()))
                  .Callback((TxId txId, uint index, TxId spendingTxId, uint height, Hash blockHash) =>
                   {
                       WatchSpends[(txId, index)] = (spendingTxId, height);
                       _undo.Add(() => WatchSpends.Remove((txId, index)));

                       // The stored watch records the spend too (as the repository's row does)
                       if (Watches.TryGetValue((txId, index), out var watch) && !watch.IsSpent)
                       {
                           watch.MarkSpent(spendingTxId, height, blockHash);
                           _undo.Add(watch.ClearSpend);
                       }

                       _pending.Add($"watch {index} spent");
                   })
                  .Returns(Task.CompletedTask);
        return repository;
    }

    private Mock<IWatchedTransactionDbRepository> CreateWatchedTransactions()
    {
        var repository = new Mock<IWatchedTransactionDbRepository>();
        repository.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) => TransactionWatches.GetValueOrDefault(txId));
        return repository;
    }

    private Mock<IBroadcastTransactionDbRepository> CreateBroadcasts()
    {
        var repository = new Mock<IBroadcastTransactionDbRepository>();
        repository.Setup(r => r.Add(It.IsAny<BroadcastTransactionModel>()))
                  .Callback<BroadcastTransactionModel>(b =>
                   {
                       Broadcasts.Add(b);
                       _undo.Add(() => Broadcasts.Remove(b));
                       _pending.Add($"broadcast {b.Purpose}");
                   });
        repository.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) => Broadcasts.FirstOrDefault(b => b.TransactionId == txId));
        repository.Setup(r => r.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                  .ReturnsAsync((ChannelId id) => Broadcasts.Where(b => b.ChannelId == id).ToList());
        repository.Setup(r => r.GetPendingAsync())
                  .ReturnsAsync(() => Broadcasts.Where(b => b.State == BroadcastState.Pending).ToList());
        repository.Setup(r => r.MarkAbandonedAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) =>
                   {
                       var broadcast = Broadcasts.FirstOrDefault(b => b.TransactionId == txId);
                       if (broadcast is not { State: BroadcastState.Pending })
                           return false;

                       broadcast.MarkAbandoned();
                       _pending.Add("broadcast abandoned");
                       return true;
                   });
        repository.Setup(r => r.MarkPendingAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) =>
                   {
                       var broadcast = Broadcasts.FirstOrDefault(b => b.TransactionId == txId);
                       if (broadcast is not { State: BroadcastState.Abandoned or BroadcastState.Replaced })
                           return false;

                       broadcast.MarkPending();
                       _pending.Add("broadcast pending");
                       return true;
                   });
        return repository;
    }

    private Mock<IChannelDbRepository> CreateChannels()
    {
        var repository = new Mock<IChannelDbRepository>();
        repository.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                  .Callback<ChannelModel>(c =>
                   {
                       PersistedChannelStates.Add(c.State);
                       _pending.Add($"channel {c.State}");
                   })
                  .Returns(Task.CompletedTask);
        repository.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>()))
                  .ReturnsAsync((ChannelId id) => LoadChannel?.Invoke(id));
        return repository;
    }

    private Mock<IRevokedCommitmentDbRepository> CreateRevocationLog()
    {
        var repository = new Mock<IRevokedCommitmentDbRepository>();
        repository.Setup(r => r.GetAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
                  .ReturnsAsync((ChannelId id, ulong number) => RevocationLog.GetValueOrDefault((id, number)));
        repository.Setup(r => r.GetLogStartAsync(It.IsAny<ChannelId>())).ReturnsAsync(() => RevocationLogStart);
        repository.Setup(r => r.DeleteByChannelIdAsync(It.IsAny<ChannelId>()))
                  .Callback<ChannelId>(id =>
                   {
                       DeletedRevocationLogs.Add(id);
                       _pending.Add("revocation log deleted");
                   })
                  .Returns(Task.CompletedTask);
        return repository;
    }

    private Mock<ILiquidityPurchaseDbRepository> CreatePurchases()
    {
        var repository = new Mock<ILiquidityPurchaseDbRepository>();
        repository.Setup(r => r.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                  .ReturnsAsync((ChannelId id) => Purchases.Where(p => p.ChannelId == id).ToList());
        repository.Setup(r => r.Update(It.IsAny<LiquidityPurchaseModel>()))
                  .Callback((LiquidityPurchaseModel p) => _pending.Add($"liquidity purchase {p.Id} {p.Status}"));
        return repository;
    }

    private Mock<IInteractiveTxSessionDbRepository> CreateInteractiveTxSessions()
    {
        var repository = new Mock<IInteractiveTxSessionDbRepository>();
        repository.Setup(r => r.DeleteByChannelIdAsync(It.IsAny<ChannelId>()))
                  .Callback<ChannelId>(id =>
                   {
                       DeletedInteractiveTxSessions.Add(id);
                       _undo.Add(() => DeletedInteractiveTxSessions.Remove(id));
                       _pending.Add("interactive-tx sessions deleted");
                   })
                  .ReturnsAsync((ChannelId _) => 0);
        return repository;
    }

    private Mock<IChannelStateDbRepository> CreateChannelState()
    {
        var repository = new Mock<IChannelStateDbRepository>();
        repository.Setup(r => r.GetHtlcOriginAsync(It.IsAny<ChannelId>(), It.IsAny<HtlcKey>()))
                  .ReturnsAsync((ChannelId channelId, HtlcKey key) =>
                   {
                       var keyBytes = (ChannelId: channelId, key.Direction, key.Id);
                       return Origins.TryGetValue(keyBytes, out var origin) ? origin : null;
                   });
        return repository;
    }

    private Mock<IInvoiceDbRepository> CreateInvoices()
    {
        var repository = new Mock<IInvoiceDbRepository>();
        repository.Setup(r => r.GetByPaymentHashAsync(It.IsAny<Hash>()))
                  .ReturnsAsync((Hash paymentHash) => Invoices.GetValueOrDefault(paymentHash));
        return repository;
    }

    private Mock<IForwardCircuitDbRepository> CreateCircuits()
    {
        var repository = new Mock<IForwardCircuitDbRepository>();
        repository.Setup(r => r.GetByIncomingAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
                  .ReturnsAsync((ChannelId channelId, ulong htlcId) =>
                   {
                       var keyBytes = (IncomingChannelId: channelId, IncomingHtlcId: htlcId);
                       return Circuits.TryGetValue(keyBytes, out var circuit) ? circuit : null;
                   });
        return repository;
    }

    private sealed class ResolutionRepository(OnchainTestStore store) : IOnchainResolutionDbRepository
    {
        public Task UpsertCloseAsync(ChannelCloseModel close)
        {
            var previous = store.Closes.GetValueOrDefault(close.ChannelId);
            store.Closes[close.ChannelId] = close;
            store._undo.Add(() =>
            {
                if (previous is null)
                    store.Closes.Remove(close.ChannelId);
                else
                    store.Closes[close.ChannelId] = previous;
            });
            store._pending.Add("close");
            return Task.CompletedTask;
        }

        public Task<ChannelCloseModel?> GetCloseAsync(ChannelId channelId) =>
            Task.FromResult(store.Closes.GetValueOrDefault(channelId));

        public Task<IReadOnlyList<ChannelCloseModel>> GetClosesAsync() =>
            Task.FromResult<IReadOnlyList<ChannelCloseModel>>(store.Closes.Values.ToList());

        public Task DeleteCloseAsync(ChannelId channelId)
        {
            store.Closes.Remove(channelId);
            return Task.CompletedTask;
        }

        public Task UpsertOutputAsync(OutputResolutionModel output)
        {
            var key = (output.TransactionId, output.OutputIndex);
            var previous = store.Outputs.GetValueOrDefault(key);
            store.Outputs[key] = output;
            store._undo.Add(() =>
            {
                if (previous is null)
                    store.Outputs.Remove(key);
                else
                    store.Outputs[key] = previous;
            });
            store._pending.Add($"output {output.OutputIndex} {output.State}");
            return Task.CompletedTask;
        }

        public Task<OutputResolutionModel?> GetOutputAsync(TxId transactionId, uint outputIndex) =>
            Task.FromResult(store.Outputs.GetValueOrDefault((transactionId, outputIndex)));

        public Task<IReadOnlyList<OutputResolutionModel>> GetOutputsByChannelIdAsync(ChannelId channelId) =>
            Task.FromResult<IReadOnlyList<OutputResolutionModel>>(
                store.Outputs.Values.Where(o => o.ChannelId == channelId).OrderBy(o => o.OutputIndex).ToList());

        public Task<IReadOnlyList<OutputResolutionModel>> GetUnresolvedOutputsAsync() =>
            Task.FromResult<IReadOnlyList<OutputResolutionModel>>(
                store.Outputs.Values.Where(o => o.State is not (OutputResolutionState.Irrevocable
                                                                or OutputResolutionState.Ignored))
                     .ToList());
    }

    /// <summary>The committed accounting events (without their save index).</summary>
    public IReadOnlyList<AccountingEventModel> Events => AccountingEvents.Select(e => e.Event).ToList();

    /// <summary>
    /// The accounting feed of the store (NL-602): staged until the save, committed with it, dropped by a failed one.
    /// Only what the writers use is implemented.
    /// </summary>
    private sealed class AccountingRepository(OnchainTestStore store) : IAccountingEventDbRepository
    {
        private IEnumerable<AccountingEventModel> All =>
            store.AccountingEvents.Select(e => e.Event).Concat(store._stagedEvents)
                 .Where(e => (e.Flags & AccountingEventFlags.Duplicate) == 0);

        public void Add(AccountingEventModel accountingEvent) => store._stagedEvents.Add(accountingEvent);

        public Task<bool> ExistsAsync(string eventKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(All.Any(e => e.EventKey == eventKey));

        public Task<AccountingEventModel?> GetByKeyAsync(string eventKey,
                                                         CancellationToken cancellationToken = default) =>
            Task.FromResult(All.FirstOrDefault(e => e.EventKey == eventKey));

        public Task<IReadOnlyList<AccountingEventModel>> GetUnsealedAsync(int max,
                                                                           CancellationToken cancellationToken =
                                                                               default) =>
            throw new NotSupportedException();

        public Task<AccountingChainTip> GetChainTipAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlySet<string>> GetSealedKeysAsync(IReadOnlyCollection<string> eventKeys,
                                                             CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ApplySealsAsync(IReadOnlyList<AccountingSeal> seals,
                                    CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountingEventModel>> ListAsync(AccountingEventQuery query,
                                                                   CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountingEventModel>> GetSealedRangeAsync(
            long fromLedgerSeq, int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AccountingEventModel>> GetAtOrAboveHeightAsync(
            uint height, IReadOnlyCollection<AccountingEventKind> kinds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>(
                All.Where(e => e.BlockHeight >= height && kinds.Contains(e.Kind)).ToList());

        public Task<IReadOnlyList<AccountingEventModel>> GetByKeyPrefixAsync(
            string keyPrefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>(
                All.Where(e => e.EventKey.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList());
    }

    /// <summary>A block hash for tests.</summary>
    public static Hash BlockHash(byte tag) => new(Enumerable.Repeat(tag, 32).ToArray());
}