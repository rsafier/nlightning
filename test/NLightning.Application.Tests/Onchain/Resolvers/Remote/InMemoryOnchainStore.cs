namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;

/// <summary>
/// The tables of one node the resolver tests need, with the EF repositories' visibility rules for the on-chain ones:
/// writes are staged until the unit of work saves; a read by key sees what this unit of work staged
/// (<c>FindAsync</c>), a list read (<c>AsNoTracking</c>) sees only saved rows. Channels, HTLC origins, circuits and
/// payments are read-only fixtures, except the snapshot writes <c>IChannelStateDbRepository.ApplyAsync</c> stages.
/// </summary>
internal sealed class InMemoryOnchainStore
{
    public Dictionary<(TxId, uint), OutputResolutionModel> Outputs { get; } = [];
    public Dictionary<(TxId, uint), WatchedOutpointModel> Watches { get; } = [];
    public Dictionary<TxId, BroadcastTransactionModel> Broadcasts { get; } = [];
    public Dictionary<ChannelId, ChannelModel> Channels { get; } = [];
    public Dictionary<(ChannelId, HtlcKey), HtlcOrigin> Origins { get; } = [];
    public List<ForwardCircuitModel> Circuits { get; } = [];
    public Dictionary<Hash, PaymentModel> Payments { get; } = [];
    public Dictionary<Hash, InvoiceModel> Invoices { get; } = [];
    public int Saves { get; private set; }

    public (IUnitOfWork UnitOfWork, Func<Task> Save) CreateUnitOfWork()
    {
        var staging = new StagingRepositories(this);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(staging);
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(staging);
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(staging);

        var channels = new Mock<IChannelDbRepository>();
        channels.Setup(c => c.GetByIdAsync(It.IsAny<ChannelId>()))
                .ReturnsAsync((ChannelId id) => Channels.GetValueOrDefault(id));
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channels.Object);

        var states = new Mock<IChannelStateDbRepository>();
        states.Setup(s => s.GetHtlcOriginAsync(It.IsAny<ChannelId>(), It.IsAny<HtlcKey>()))
              .ReturnsAsync((ChannelId id, HtlcKey key) => Origins.TryGetValue((id, key), out var origin)
                                                               ? origin
                                                               : (HtlcOrigin?)null);
        states.Setup(s => s.FindHtlcsByOriginAsync(It.IsAny<HtlcOrigin>()))
              .ReturnsAsync((HtlcOrigin origin) => Origins.Where(o => o.Value == origin)
                                                          .Select(o => (o.Key.Item1, o.Key.Item2))
                                                          .ToList());
        states.Setup(s => s.LoadAsync(It.IsAny<ChannelId>(), It.IsAny<CommitmentParams>()))
              .ReturnsAsync((PersistedChannelState?)null);
        states.Setup(s => s.ApplyAsync(It.IsAny<ChannelCommitments>(), It.IsAny<ChannelTransition>(),
                                       It.IsAny<ChannelStateExtras?>()))
              .Callback<ChannelCommitments, ChannelTransition, ChannelStateExtras?>(
                   (next, _, _) => staging.StagedSnapshots.Add(next))
              .Returns(Task.CompletedTask);
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(states.Object);

        var circuits = new Mock<IForwardCircuitDbRepository>();
        circuits.Setup(c => c.GetByIncomingAsync(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
                .ReturnsAsync((ChannelId id, ulong htlcId) =>
                                  Circuits.FirstOrDefault(c => c.IncomingChannelId == id
                                                            && c.IncomingHtlcId == htlcId));
        unitOfWork.SetupGet(u => u.ForwardCircuitDbRepository).Returns(circuits.Object);

        var payments = new Mock<IPaymentDbRepository>();
        payments.Setup(p => p.GetByPaymentHashAsync(It.IsAny<Hash>()))
                .ReturnsAsync((Hash hash) => Payments.GetValueOrDefault(hash));
        unitOfWork.SetupGet(u => u.PaymentDbRepository).Returns(payments.Object);

        var invoices = new Mock<IInvoiceDbRepository>();
        invoices.Setup(i => i.GetByPaymentHashAsync(It.IsAny<Hash>()))
                .ReturnsAsync((Hash hash) => Invoices.GetValueOrDefault(hash));
        unitOfWork.SetupGet(u => u.InvoiceDbRepository).Returns(invoices.Object);

        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(SaveAsync);
        return (unitOfWork.Object, SaveAsync);

        Task SaveAsync()
        {
            staging.Commit();
            Saves++;
            return Task.CompletedTask;
        }
    }

    /// <summary>The chain monitor records a spend of a watched outpoint (saved).</summary>
    public void MarkSpent(TxId txId, uint vout, TxId spender, uint height)
    {
        if (Watches.TryGetValue((txId, vout), out var watch))
            watch.MarkSpent(spender, height, new Hash(new byte[32]));
    }

    private sealed class StagingRepositories(InMemoryOnchainStore store)
        : IOnchainResolutionDbRepository, IWatchedOutpointDbRepository, IBroadcastTransactionDbRepository
    {
        private readonly Dictionary<(TxId, uint), OutputResolutionModel> _outputs = [];
        private readonly Dictionary<(TxId, uint), WatchedOutpointModel> _watches = [];
        private readonly Dictionary<TxId, BroadcastTransactionModel> _broadcasts = [];

        public List<ChannelCommitments> StagedSnapshots { get; } = [];

        public void Commit()
        {
            foreach (var (key, value) in _outputs)
                store.Outputs[key] = value;
            foreach (var (key, value) in _watches)
                store.Watches[key] = value;
            foreach (var (key, value) in _broadcasts)
                store.Broadcasts[key] = value;
            foreach (var snapshot in StagedSnapshots)
                store.Channels[snapshot.ChannelId].UpdateCommitments(snapshot);
            _outputs.Clear();
            _watches.Clear();
            _broadcasts.Clear();
            StagedSnapshots.Clear();
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

        public Task<IReadOnlyList<BroadcastTransactionModel>> GetByChannelIdAsync(ChannelId channelId) =>
            Task.FromResult<IReadOnlyList<BroadcastTransactionModel>>(
                store.Broadcasts.Values.Concat(_broadcasts.Values)
                     .Where(b => b.ChannelId == channelId)
                     .DistinctBy(b => b.TransactionId)
                     .OrderBy(b => b.CreatedAt)
                     .ToList());

        public Task<bool> MarkAbandonedAsync(TxId transactionId) => throw new NotSupportedException();

        public Task<bool> MarkReplacedAsync(TxId transactionId) => throw new NotSupportedException();

        public Task<bool> MarkPendingAsync(TxId transactionId) => throw new NotSupportedException();

        public Task<IReadOnlyList<BroadcastTransactionModel>> GetPendingAsync() => throw new NotSupportedException();

        public Task MarkConfirmedAsync(TxId transactionId, uint height, Hash blockHash) =>
            throw new NotSupportedException();

        public Task<int> UnconfirmAboveAsync(uint height) => throw new NotSupportedException();
    }
}