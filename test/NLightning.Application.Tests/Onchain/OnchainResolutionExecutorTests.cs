using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Fees;
using Channels.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT 5 plan §3.2 steps 4-5, O6-T2: <see cref="OnchainResolutionExecutor"/> applies a resolver's actions in one save
/// and only then publishes, watches, raises and alerts; marks spent outputs resolved, makes them irrevocable at depth
/// 100 (not 99) and closes the channel once everything is irrevocable.
/// </summary>
public sealed class OnchainResolutionExecutorTests : IDisposable
{
    private const uint SpentAt = 1_000;

    private static readonly TxId s_commitmentTxId = new(Enumerable.Repeat((byte)0xC0, 32).ToArray());

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChainBroadcaster> _broadcaster = new();
    private readonly Mock<IOutpointWatcher> _outpointWatcher = new();
    private readonly Mock<IHtlcSwitch> _htlcSwitch = new();
    private readonly FakeResolver _resolver = new();
    private readonly FakeSweepScheduler _sweepScheduler = new();
    private readonly FakeBitcoinChain _chain = new(SpentAt - 1);
    private readonly List<string> _calls = [];
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;
    private bool _channelLoaded = true;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public OnchainResolutionExecutorTests()
    {
        _channel = _pair.Alice.Channel;
        _channel.UpdateState(ChannelState.OnchainResolving);
        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channelLoaded && id == _channel.ChannelId ? _channel : null;
                    return channel is not null;
                }));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) =>
                            _channelLoaded && predicate(_channel) ? [_channel] : []);
        _memory.Setup(m => m.TryRemoveChannel(It.IsAny<ChannelId>())).Callback(() => _channelLoaded = false);
        _broadcaster.Setup(b => b.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                    .Callback<BroadcastTransactionModel>(b => _calls.Add($"publish {b.Purpose}"))
                    .ReturnsAsync(true);
        _outpointWatcher.Setup(w => w.TrackWatchedOutpoint(It.IsAny<WatchedOutpointModel>()))
                        .Callback(() => _calls.Add("track"));
        _htlcSwitch.Setup(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()))
                   .Callback(() => _calls.Add("switch"))
                   .Returns(Task.CompletedTask);

        _store.OnSave = () => _calls.Add("save");

        // The database's copy of the channel: another instance than the shared in-memory model
        _store.LoadChannel = id => id == _pair.Bob.Channel.ChannelId ? _pair.Bob.Channel : null;
        var unitOfWork = _store.CreateUnitOfWork();

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddScoped<IOutputResolver>(_ => _resolver);
        services.AddSingleton<ISweepScheduler>(_sweepScheduler);
        services.AddSingleton(_htlcSwitch.Object);
        services.AddSingleton<IBitcoinChainService>(_chain);
        _provider = services.BuildServiceProvider();

        _store.Closes[_channel.ChannelId] = new ChannelCloseModel(_channel.ChannelId,
                                                                  ChannelCloseKind.LocalCommitment, s_commitmentTxId,
                                                                  3, SpentAt, OnchainTestStore.BlockHash(1),
                                                                  DateTimeOffset.UtcNow);
    }

    private OnchainResolutionExecutor CreateExecutor(uint irrevocableDepth = 100) =>
        new(_broadcaster.Object, new ChannelLockProvider(), _memory.Object,
            NullLogger<OnchainResolutionExecutor>.Instance, _outpointWatcher.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OnchainOptions { IrrevocableDepth = irrevocableDepth }));

    [Fact]
    public async Task Given_ResolverActions_When_Round_Then_OneSaveThenPublishTrackRaiseInThatOrder()
    {
        // Arrange: the resolver sweeps output 0 (row + broadcast), watches the sweep's output and raises an event
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var sweep = CreateBroadcast(0x51);
        var channelEvent = new OutgoingHtlcFulfilled(_channel.ChannelId, 4, new Hash(new byte[32]),
                                                     new Secret(new byte[32]));
        _resolver.OnResolve = (_, outputs, _) =>
        [
            new UpsertOutputAction(outputs[0] with
            {
                State = OutputResolutionState.Broadcast, ResolvingTransactionId = sweep.TransactionId
            }),
            new BroadcastAction(sweep),
            new WatchOutpointAction(new WatchedOutpointModel(sweep.TransactionId, 0, _channel.ChannelId,
                                                             WatchedOutpointPurpose.ResolutionOutput)),
            new RaiseChannelEventAction(channelEvent),
            new AlertAction("B5-TEST", "just a test")
        ];

        // Act
        await CreateExecutor().RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);

        // Assert: the row, the broadcast row and the watch in one save; then publish, track, switch
        var save = Assert.Single(_store.Saves);
        Assert.Equal(["output 0 Broadcast", "broadcast Sweep", "watch 0"], save);
        Assert.Equal(["save", "track", "publish Sweep", "switch"], _calls);
        Assert.Equal(OutputResolutionState.Broadcast, _store.Outputs.Values.Single().State);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        Assert.Equal(SpentAt + 5, _resolver.LastHeight);
    }

    [Fact]
    public async Task Given_ResolverRepeatsAPendingBroadcast_When_NextRound_Then_NotStoredAgainButPublishedAgain()
    {
        // Arrange (planner rule: an action repeats until its effect is on chain)
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var sweep = CreateBroadcast(0x52);
        _resolver.OnResolve = (_, _, _) => [new BroadcastAction(sweep)];
        var executor = CreateExecutor();
        await executor.RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);
        _calls.Clear();

        // Act
        await executor.RunRoundAsync(SpentAt + 6, TestContext.Current.CancellationToken);

        // Assert: one row only, nothing saved by the second round, sent again
        Assert.Single(_store.Broadcasts);
        Assert.Single(_store.Saves);
        Assert.Equal(["publish Sweep"], _calls);
    }

    [Fact]
    public async Task Given_OutputSpent_When_Handled_Then_ResolvedAtThatHeightAndResolverToldWithTheSpender()
    {
        // Arrange
        AddOutput(1, OutputDescriptorKind.LocalOfferedHtlc);
        var spender = CreateSpend(s_commitmentTxId, 1);
        var args = new OutpointSpentEventArgs(_channel.ChannelId, spender, SpentAt + 10, 2, s_commitmentTxId, 1,
                                              OnchainTestStore.BlockHash(2));
        var executor = CreateExecutor();

        // Act
        await executor.HandleOutputSpentAsync(args, TestContext.Current.CancellationToken);
        await executor.HandleOutputSpentAsync(args, TestContext.Current.CancellationToken);

        // Assert: resolved at the spend height, the resolver got the resolved row and the parsed spender; the replay
        // wrote nothing new
        var row = _store.Outputs.Values.Single();
        Assert.Equal(OutputResolutionState.Resolved, row.State);
        Assert.Equal(SpentAt + 10, row.ResolvedHeight);
        var (spentRow, tx, height) = _resolver.Spends[0];
        Assert.Equal(OutputResolutionState.Resolved, spentRow.State);
        Assert.Equal(spender.TxId, tx.TxId);
        Assert.Equal(SpentAt + 10, height);
        Assert.Equal(2, _resolver.Spends.Count);
        Assert.Single(_store.Saves);
    }

    [Fact]
    public async Task Given_ResolvedOutputs_When_99And100BlocksDeep_Then_IrrevocableAt100AndChannelClosed()
    {
        // Arrange (O6-T2, B5-GEN-02): both outputs were resolved at SpentAt + 1; the resolver does nothing
        AddOutput(0, OutputDescriptorKind.DelayedToLocal, OutputResolutionState.Resolved, SpentAt + 1);
        AddOutput(2, OutputDescriptorKind.PaymentToRemote, OutputResolutionState.Ignored);
        var executor = CreateExecutor();

        // Act: 99 deep
        await executor.RunRoundAsync(SpentAt + 99, TestContext.Current.CancellationToken);

        // Assert: nothing yet
        Assert.Equal(OutputResolutionState.Resolved, _store.Outputs[(s_commitmentTxId, 0)].State);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        Assert.Empty(_store.Saves);

        // Act: 100 deep (the funding spend is 101 deep)
        await executor.RunRoundAsync(SpentAt + 100, TestContext.Current.CancellationToken);

        // Assert: irrevocable, Closed and the revocation log dropped, all in one save; forgotten in memory
        Assert.Equal(OutputResolutionState.Irrevocable, _store.Outputs[(s_commitmentTxId, 0)].State);
        Assert.Equal(ChannelState.Closed, _channel.State);
        var save = Assert.Single(_store.Saves);
        Assert.Equal(["output 0 Irrevocable", "channel Closed", "revocation log deleted"], save);
        _memory.Verify(m => m.TryRemoveChannel(_channel.ChannelId), Times.Once);
    }

    [Fact]
    public async Task Given_CloseWithoutOutputs_When_FundingSpend100Deep_Then_ClosedNotBefore()
    {
        // Arrange (an unknown spend: nothing to resolve)
        var executor = CreateExecutor();

        // Act / Assert
        await executor.RunRoundAsync(SpentAt + 98, TestContext.Current.CancellationToken);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        await executor.RunRoundAsync(SpentAt + 99, TestContext.Current.CancellationToken);
        Assert.Equal(ChannelState.Closed, _channel.State);
    }

    [Fact]
    public async Task Given_NoResolverForTheCloseKind_When_Rounds_Then_OutputsStayPendingAndChannelResolving()
    {
        // Arrange
        _resolver.Kinds = [ChannelCloseKind.RevokedCommitment];
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(SpentAt + 500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OutputResolutionState.Pending, _store.Outputs.Values.Single().State);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        Assert.Null(_resolver.LastHeight);
    }

    [Fact]
    public async Task Given_SaveFails_When_Round_Then_NothingPublishedTrackedOrRaised()
    {
        // Arrange
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var sweep = CreateBroadcast(0x53);
        _resolver.OnResolve = (_, _, _) =>
        [
            new BroadcastAction(sweep),
            new RaiseChannelEventAction(new OutgoingHtlcFulfilled(_channel.ChannelId, 4, new Hash(new byte[32]),
                                                                  new Secret(new byte[32])))
        ];
        _store.FailNextSave = new InvalidOperationException("database down");

        // Act
        await CreateExecutor().RunRoundAsync(SpentAt + 5, TestContext.Current.CancellationToken);

        // Assert (D4: persist before broadcast)
        Assert.Empty(_store.Broadcasts);
        Assert.DoesNotContain(_calls, c => c.StartsWith("publish", StringComparison.Ordinal) || c == "switch");
    }

    [Fact]
    public async Task Given_SaveFailsAtTheIrrevocableDepth_When_NextRound_Then_ChannelStillResolvingThenClosed()
    {
        // Arrange (NL-282 class): everything is irrevocable at SpentAt + 100, and that round's save fails
        AddOutput(0, OutputDescriptorKind.DelayedToLocal, OutputResolutionState.Resolved, SpentAt + 1);
        var executor = CreateExecutor();
        _store.FailNextSave = new InvalidOperationException("database down");

        // Act
        await executor.RunRoundAsync(SpentAt + 100, TestContext.Current.CancellationToken);

        // Assert: the shared model is still resolving and still loaded, so the next round sees it
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        _memory.Verify(m => m.TryRemoveChannel(It.IsAny<ChannelId>()), Times.Never);
        Assert.Empty(_store.Saves);

        // Act: the next block
        await executor.RunRoundAsync(SpentAt + 101, TestContext.Current.CancellationToken);

        // Assert: closed now, in one save
        Assert.Equal(ChannelState.Closed, _channel.State);
        Assert.Equal(["output 0 Irrevocable", "channel Closed", "revocation log deleted"], Assert.Single(_store.Saves));
        _memory.Verify(m => m.TryRemoveChannel(_channel.ChannelId), Times.Once);
    }

    [Fact]
    public async Task Given_CommitmentOutputsSpentInItsBlockAndTheNext_When_WatchesCaughtUp_Then_BothResolved()
    {
        // Arrange (BOLT 5 "monitor every output not irrevocably resolved"): the commitment and a spend of its vout 0
        // in one block, a spend of its vout 1 in the next; both blocks were processed before the watches existed
        var commitment = CreateTransaction(new TxId(Enumerable.Repeat((byte)0xF0, 32).ToArray()), 0, outputs: 2);
        var commitmentTxId = new TxId(commitment.GetHash().ToBytes());
        var spend0 = CreateTransaction(commitmentTxId, 0);
        var spend1 = CreateTransaction(commitmentTxId, 1);
        _chain.Mine(commitment, spend0);
        _chain.Mine(spend1);
        _chain.Mine();
        UseClose(commitmentTxId, SpentAt);
        AddOutput(0, OutputDescriptorKind.LocalOfferedHtlc, txId: commitmentTxId);
        AddOutput(1, OutputDescriptorKind.LocalReceivedHtlc, txId: commitmentTxId);
        var watches = new[]
        {
            new WatchedOutpointModel(commitmentTxId, 0, _channel.ChannelId, WatchedOutpointPurpose.ResolutionOutput),
            new WatchedOutpointModel(commitmentTxId, 1, _channel.ChannelId, WatchedOutpointPurpose.ResolutionOutput)
        };

        // Act
        await CreateExecutor().CatchUpSpendsAsync(_channel.ChannelId, watches, SpentAt,
                                                  TestContext.Current.CancellationToken);

        // Assert: each resolved at its own block, the resolver saw both spenders, the watches record the spends
        Assert.Equal(OutputResolutionState.Resolved, _store.Outputs[(commitmentTxId, 0)].State);
        Assert.Equal(SpentAt, _store.Outputs[(commitmentTxId, 0)].ResolvedHeight);
        Assert.Equal(OutputResolutionState.Resolved, _store.Outputs[(commitmentTxId, 1)].State);
        Assert.Equal(SpentAt + 1, _store.Outputs[(commitmentTxId, 1)].ResolvedHeight);
        Assert.Equal([spend0.GetHash(), spend1.GetHash()],
                     _resolver.Spends.Select(x => new uint256(x.Spender.TxId)).ToArray());
        Assert.Equal((new TxId(spend0.GetHash().ToBytes()), SpentAt), _store.WatchSpends[(commitmentTxId, 0)]);
        Assert.Equal((new TxId(spend1.GetHash().ToBytes()), SpentAt + 1), _store.WatchSpends[(commitmentTxId, 1)]);
        Assert.All(_store.Saves, save => Assert.Contains(save, w => w.EndsWith(" spent", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Given_ResolverWatchesAnOutputAlreadySpent_When_Round_Then_TheSpendIsFoundAndResolved()
    {
        // Arrange: the resolver starts watching commitment output 2, which was spent two blocks before this round
        var commitment = CreateTransaction(new TxId(Enumerable.Repeat((byte)0xF1, 32).ToArray()), 0, outputs: 3);
        var commitmentTxId = new TxId(commitment.GetHash().ToBytes());
        var spend2 = CreateTransaction(commitmentTxId, 2);
        _chain.Mine(commitment);
        _chain.Mine();
        _chain.Mine(spend2);
        UseClose(commitmentTxId, SpentAt);
        AddOutput(2, OutputDescriptorKind.PaymentToRemote, txId: commitmentTxId);
        var watch = new WatchedOutpointModel(commitmentTxId, 2, _channel.ChannelId,
                                             WatchedOutpointPurpose.ResolutionOutput);
        _resolver.OnResolve = (_, _, _) => [new WatchOutpointAction(watch)];

        // Act
        await CreateExecutor().RunRoundAsync(SpentAt + 3, TestContext.Current.CancellationToken);

        // Assert: tracked, then the mined spend handled like the monitor's event
        _outpointWatcher.Verify(w => w.TrackWatchedOutpoint(watch), Times.Once);
        Assert.Equal(OutputResolutionState.Resolved, _store.Outputs[(commitmentTxId, 2)].State);
        Assert.Equal(SpentAt + 2, _store.Outputs[(commitmentTxId, 2)].ResolvedHeight);
        Assert.Equal(new uint256(spend2.GetHash()), new uint256(Assert.Single(_resolver.Spends).Spender.TxId));
    }

    [Fact]
    public async Task Given_ScheduledRounds_When_Idle_Then_TheLatestHeightRan()
    {
        // Arrange
        var executor = CreateExecutor();

        // Act
        executor.ScheduleRound(SpentAt + 1);
        executor.ScheduleRound(SpentAt + 2);
        await executor.WhenIdleAsync();

        // Assert
        Assert.Equal(SpentAt + 2, _resolver.LastHeight);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private void UseClose(TxId commitmentTxId, uint height) =>
        _store.Closes[_channel.ChannelId] = _store.Closes[_channel.ChannelId] with
        {
            CommitmentTransactionId = commitmentTxId,
            SpentAtHeight = height
        };

    private static Transaction CreateTransaction(TxId spent, uint vout, int outputs = 1)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(spent), vout));
        for (var i = 0; i < outputs; i++)
            transaction.Outputs.Add(Money.Satoshis(10_000 + i), new Key().PubKey.WitHash.ScriptPubKey);
        return transaction;
    }

    private void AddOutput(uint vout, OutputDescriptorKind kind,
                           OutputResolutionState state = OutputResolutionState.Pending, uint? resolvedHeight = null,
                           TxId? txId = null)
    {
        _store.Outputs[(txId ?? s_commitmentTxId, vout)] = new OutputResolutionModel
        {
            TransactionId = txId ?? s_commitmentTxId,
            OutputIndex = vout,
            ChannelId = _channel.ChannelId,
            Descriptor = kind,
            State = state,
            ResolvedHeight = resolvedHeight
        };
    }

    private BroadcastTransactionModel CreateBroadcast(byte seed)
    {
        var transaction = CreateSpend(new TxId(Enumerable.Repeat(seed, 32).ToArray()), 0);
        return new BroadcastTransactionModel(transaction, BroadcastPurpose.Sweep, _channel.ChannelId, SpentAt);
    }

    private static SignedTransaction CreateSpend(TxId spent, uint vout)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(spent), vout));
        transaction.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        return new SignedTransaction(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());
    }

    [Fact]
    public async Task Given_SchedulerReplacesASweep_When_Round_Then_ReplacementSavedWithTheResolverActionsThenPublished()
    {
        // Arrange: a pending sweep the scheduler replaces (O6-T1)
        AddOutput(0, OutputDescriptorKind.DelayedToLocal, OutputResolutionState.Broadcast);
        var replacement = CreateBroadcast(0x61);
        _sweepScheduler.OnPlan = (_, outputs, height) =>
        [
            new UpsertOutputAction(outputs[0] with { ResolvingTransactionId = replacement.TransactionId }),
            new BroadcastAction(replacement)
        ];

        // Act
        await CreateExecutor().RunRoundAsync(SpentAt + 40, TestContext.Current.CancellationToken);

        // Assert: planned after the resolver, at the round's height, with the resolver's rows; one save, then publish
        Assert.Equal(SpentAt + 40, _sweepScheduler.LastHeight);
        Assert.Equal(["output 0 Broadcast", "broadcast Sweep"], Assert.Single(_store.Saves));
        Assert.Equal(["save", "publish Sweep"], _calls);
        Assert.Equal(replacement.TransactionId, _store.Outputs.Values.Single().ResolvingTransactionId);
    }

    [Fact]
    public async Task Given_SpendEvent_When_Handled_Then_SchedulerNotAsked()
    {
        // Arrange: bumping is a per-block decision, not a reaction to a spend
        AddOutput(0, OutputDescriptorKind.DelayedToLocal);
        var spend = CreateSpend(s_commitmentTxId, 0);

        // Act
        await CreateExecutor().HandleOutputSpentAsync(
            new OutpointSpentEventArgs(_channel.ChannelId, spend, SpentAt + 3, 1, s_commitmentTxId, 0,
                                       OnchainTestStore.BlockHash(3)), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(_sweepScheduler.LastHeight);
    }

    /// <summary>A sweep scheduler whose answers the test sets.</summary>
    private sealed class FakeSweepScheduler : ISweepScheduler
    {
        public Func<ChannelCloseModel, IReadOnlyList<OutputResolutionModel>, uint,
            IReadOnlyList<OutputResolverAction>>? OnPlan
        { get; set; }

        public uint? LastHeight { get; private set; }

        public Task<IReadOnlyList<OutputResolverAction>> PlanAsync(ChannelCloseModel close,
                                                                   IReadOnlyList<OutputResolutionModel> outputs,
                                                                   uint height,
                                                                   Domain.Persistence.Interfaces.IUnitOfWork unitOfWork,
                                                                   CancellationToken cancellationToken)
        {
            LastHeight = height;
            return Task.FromResult(OnPlan?.Invoke(close, outputs, height) ?? []);
        }
    }

    /// <summary>A resolver whose answers the test sets; it records what it was asked.</summary>
    private sealed class FakeResolver : IOutputResolver
    {
        public HashSet<ChannelCloseKind> Kinds { get; set; } = [ChannelCloseKind.LocalCommitment];

        public Func<ChannelCloseModel, IReadOnlyList<OutputResolutionModel>, uint,
            IReadOnlyList<OutputResolverAction>>? OnResolve
        { get; set; }

        public uint? LastHeight { get; private set; }

        public List<(OutputResolutionModel Output, ChainTx Spender, uint Height)> Spends { get; } = [];

        public bool CanResolve(ChannelCloseKind kind) => Kinds.Contains(kind);

        public Task<IReadOnlyList<OutputResolverAction>> ResolveAsync(ChannelCloseModel close,
                                                                      IReadOnlyList<OutputResolutionModel> outputs,
                                                                      uint height,
                                                                      CancellationToken cancellationToken)
        {
            LastHeight = height;
            return Task.FromResult(OnResolve?.Invoke(close, outputs, height) ?? []);
        }

        public Task<IReadOnlyList<OutputResolverAction>> OnOutputSpentAsync(
            ChannelCloseModel close, OutputResolutionModel output, ChainTx spendingTransaction, uint height,
            CancellationToken cancellationToken)
        {
            Spends.Add((output, spendingTransaction, height));
            return Task.FromResult<IReadOnlyList<OutputResolverAction>>([]);
        }
    }
}