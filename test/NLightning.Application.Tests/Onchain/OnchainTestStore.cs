namespace NLightning.Application.Tests.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

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

    public Dictionary<ChannelId, ChannelCloseModel> Closes { get; } = [];
    public Dictionary<(TxId, uint), OutputResolutionModel> Outputs { get; } = [];
    public Dictionary<(TxId, uint), WatchedOutpointModel> Watches { get; } = [];
    public List<BroadcastTransactionModel> Broadcasts { get; } = [];
    public List<ChannelState> PersistedChannelStates { get; } = [];
    public List<ChannelId> DeletedRevocationLogs { get; } = [];
    public Dictionary<(ChannelId, ulong), RevokedCommitmentModel> RevocationLog { get; } = [];

    /// <summary>The first commitment number the revocation log covers (<c>GetLogStartAsync</c>).</summary>
    public ulong RevocationLogStart { get; set; }

    /// <summary>What <c>ChannelDbRepository.GetByIdAsync</c> returns (the database's copy of a channel).</summary>
    public Func<ChannelId, ChannelModel?>? LoadChannel { get; set; }

    /// <summary>Spends recorded on watches (<c>MarkSpentAsync</c>): outpoint → (spending txid, height).</summary>
    public Dictionary<(TxId, uint), (TxId SpendingTxId, uint Height)> WatchSpends { get; } = [];

    /// <summary>Per save, the writes it committed (e.g. "close", "output 1", "watch 1", "channel OnchainResolving").
    /// </summary>
    public List<IReadOnlyList<string>> Saves { get; } = [];

    /// <summary>Called after every successful save (to order saves against other calls).</summary>
    public Action? OnSave { get; set; }

    /// <summary>Set to make the next save throw.</summary>
    public Exception? FailNextSave { get; set; }

    public Mock<IUnitOfWork> CreateUnitOfWork()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(new ResolutionRepository(this));
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(CreateWatchedOutpoints().Object);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(CreateBroadcasts().Object);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(CreateChannels().Object);
        unitOfWork.SetupGet(u => u.RevokedCommitmentDbRepository).Returns(CreateRevocationLog().Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            if (FailNextSave is { } failure)
            {
                // Nothing of a failed save is kept
                FailNextSave = null;
                _pending.Clear();
                for (var i = _undo.Count - 1; i >= 0; i--)
                    _undo[i]();
                _undo.Clear();
                throw failure;
            }

            Saves.Add(_pending.ToList());
            _pending.Clear();
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
                  .Callback((TxId txId, uint index, TxId spendingTxId, uint height, Hash _) =>
                   {
                       WatchSpends[(txId, index)] = (spendingTxId, height);
                       _undo.Add(() => WatchSpends.Remove((txId, index)));
                       _pending.Add($"watch {index} spent");
                   })
                  .Returns(Task.CompletedTask);
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

    /// <summary>A block hash for tests.</summary>
    public static Hash BlockHash(byte tag) => new(Enumerable.Repeat(tag, 32).ToArray());
}