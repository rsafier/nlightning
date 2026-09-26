namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// The on-chain tables of one node for the resolver tests, with the EF repositories' visibility rules: writes are
/// staged until <see cref="SaveAsync"/>; a read by key sees what this unit of work staged (<c>FindAsync</c>), a list
/// read (<c>AsNoTracking</c>) sees only saved rows. <see cref="CreateUnitOfWork"/> gives a fresh unit of work over it.
/// </summary>
internal sealed class InMemoryOnchainStore
{
    public Dictionary<(TxId, uint), OutputResolutionModel> Outputs { get; } = [];
    public Dictionary<(TxId, uint), WatchedOutpointModel> Watches { get; } = [];
    public Dictionary<TxId, BroadcastTransactionModel> Broadcasts { get; } = [];
    public int Saves { get; private set; }

    public (IUnitOfWork UnitOfWork, Func<Task> Save) CreateUnitOfWork()
    {
        var staging = new StagingRepositories(this);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(staging);
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(staging);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(staging);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(SaveAsync);
        return (unitOfWork.Object, SaveAsync);

        Task SaveAsync()
        {
            staging.Commit();
            Saves++;
            return Task.CompletedTask;
        }
    }

    private sealed class StagingRepositories(InMemoryOnchainStore store)
        : IOnchainResolutionDbRepository, IWatchedOutpointDbRepository, IBroadcastTransactionDbRepository
    {
        private readonly Dictionary<(TxId, uint), OutputResolutionModel> _outputs = [];
        private readonly Dictionary<(TxId, uint), WatchedOutpointModel> _watches = [];
        private readonly Dictionary<TxId, BroadcastTransactionModel> _broadcasts = [];

        public void Commit()
        {
            foreach (var (key, value) in _outputs)
                store.Outputs[key] = value;
            foreach (var (key, value) in _watches)
                store.Watches[key] = value;
            foreach (var (key, value) in _broadcasts)
                store.Broadcasts[key] = value;
            _outputs.Clear();
            _watches.Clear();
            _broadcasts.Clear();
        }

        // Output resolutions
        public Task UpsertOutputAsync(OutputResolutionModel output)
        {
            _outputs[(output.TransactionId, output.OutputIndex)] = output;
            return Task.CompletedTask;
        }

        public Task<OutputResolutionModel?> GetOutputAsync(TxId transactionId, uint outputIndex) =>
            Task.FromResult(_outputs.GetValueOrDefault((transactionId, outputIndex))
                         ?? store.Outputs.GetValueOrDefault((transactionId, outputIndex)));

        public Task<IReadOnlyList<OutputResolutionModel>> GetOutputsByChannelIdAsync(ChannelId channelId) =>
            Task.FromResult<IReadOnlyList<OutputResolutionModel>>(
                store.Outputs.Values.Where(o => o.ChannelId == channelId).OrderBy(o => o.OutputIndex).ToList());

        public Task<IReadOnlyList<OutputResolutionModel>> GetUnresolvedOutputsAsync() =>
            throw new NotSupportedException();

        public Task UpsertCloseAsync(ChannelCloseModel close) => throw new NotSupportedException();
        public Task<ChannelCloseModel?> GetCloseAsync(ChannelId channelId) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChannelCloseModel>> GetClosesAsync() => throw new NotSupportedException();
        public Task DeleteCloseAsync(ChannelId channelId) => throw new NotSupportedException();

        // Watched outpoints
        public void Add(WatchedOutpointModel watchedOutpoint) =>
            _watches[(watchedOutpoint.TransactionId, watchedOutpoint.OutputIndex)] = watchedOutpoint;

        public Task<WatchedOutpointModel?> GetAsync(TxId transactionId, uint outputIndex) =>
            Task.FromResult(_watches.GetValueOrDefault((transactionId, outputIndex))
                         ?? store.Watches.GetValueOrDefault((transactionId, outputIndex)));

        public Task<IReadOnlyList<WatchedOutpointModel>> GetActiveAsync() => throw new NotSupportedException();

        public Task MarkSpentAsync(TxId transactionId, uint outputIndex, TxId spendingTransactionId, uint height,
                                   Hash blockHash) => throw new NotSupportedException();

        public Task<int> ClearSpendsAboveAsync(uint height) => throw new NotSupportedException();

        public Task<WatchedOutpointModel?> AddFundingOutpointIfMissingAsync(ChannelId channelId,
                                                                            TxId fundingTransactionId) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WatchedOutpointModel>> AddMissingFundingOutpointsAsync() =>
            throw new NotSupportedException();

        // Broadcasts
        public void Add(BroadcastTransactionModel transaction) => _broadcasts[transaction.TransactionId] = transaction;

        public Task<BroadcastTransactionModel?> GetByTransactionIdAsync(TxId transactionId) =>
            Task.FromResult(_broadcasts.GetValueOrDefault(transactionId)
                         ?? store.Broadcasts.GetValueOrDefault(transactionId));

        public Task<IReadOnlyList<BroadcastTransactionModel>> GetPendingAsync() => throw new NotSupportedException();

        public Task MarkConfirmedAsync(TxId transactionId, uint height, Hash blockHash) =>
            throw new NotSupportedException();

        public Task<int> UnconfirmAboveAsync(uint height) => throw new NotSupportedException();
    }
}