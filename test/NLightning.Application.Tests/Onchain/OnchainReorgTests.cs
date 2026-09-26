using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Safety;
using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Reorg;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT 5 plan O6-T3 (§3.8, NL-292) over a fake chain: when the funding spend's block is reorged out the channel stays
/// <see cref="ChannelState.OnchainResolving"/> (never back to Open), its funding outpoint stays watched, nothing ages or
/// closes, and spends the chain monitor rolled back are unresolved again; our own commitment is sent again at once, the
/// peer's is waited for <see cref="OnchainOptions.ReorgGraceBlocks"/> blocks before our latest commitment is broadcast;
/// and a funding transaction confirmed again at another position moves the short channel id.
/// </summary>
public sealed class OnchainReorgTests : IDisposable
{
    private const uint SpentAt = 1_000;
    private const uint Grace = 6;

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChainBroadcaster> _broadcaster = new();
    private readonly Mock<IOutpointWatcher> _outpointWatcher = new();
    private readonly RecordingResolver _resolver = new();
    private readonly FakeBitcoinChain _chain = new(SpentAt - 1);
    private readonly List<BroadcastTransactionModel> _published = [];
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;
    private readonly TxId _commitmentTxId;
    private readonly WatchedOutpointModel _fundingWatch;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public OnchainReorgTests()
    {
        _channel = _pair.Alice.Channel;
        _channel.UpdateCommitments(_pair.Alice.State);
        _channel.UpdateState(ChannelState.OnchainResolving);
        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = id == _channel.ChannelId ? _channel : null;
                    return channel is not null;
                }));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => predicate(_channel) ? [_channel] : []);
        _broadcaster.Setup(b => b.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                    .Callback<BroadcastTransactionModel>(_published.Add)
                    .ReturnsAsync(true);

        _store.LoadChannel = id => id == _channel.ChannelId ? _channel : null;
        var unitOfWork = _store.CreateUnitOfWork();
        var broadcasts = Mock.Get(unitOfWork.Object.BroadcastTransactionDbRepository);
        broadcasts.Setup(r => r.MarkPendingAsync(It.IsAny<TxId>()))
                  .ReturnsAsync((TxId txId) =>
                   {
                       var row = _store.Broadcasts.FirstOrDefault(b => b.TransactionId == txId);
                       if (row is not { State: BroadcastState.Abandoned or BroadcastState.Replaced })
                           return false;
                       row.MarkPending();
                       return true;
                   });

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton(_pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddSingleton<LocalCommitmentBroadcastBuilder>();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddScoped<IOutputResolver>(_ => _resolver);
        services.AddSingleton<IBitcoinChainService>(_chain);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        _provider = services.BuildServiceProvider();

        // The peer's commitment confirmed at SpentAt; the funding outpoint is watched
        var commitment = Network.RegTest.CreateTransaction();
        commitment.Inputs.Add(new OutPoint(new uint256((byte[])_channel.FundingOutput!.TransactionId!.Value),
                                           _channel.FundingOutput.Index!.Value));
        commitment.Outputs.Add(Money.Satoshis(500_000), new Key().PubKey.WitHash.ScriptPubKey);
        var block = _chain.Mine(commitment);
        _commitmentTxId = new TxId(commitment.GetHash().ToBytes());
        _fundingWatch = new WatchedOutpointModel(_channel.FundingOutput.TransactionId.Value,
                                                 _channel.FundingOutput.Index.Value, _channel.ChannelId,
                                                 WatchedOutpointPurpose.FundingOutput);
        _fundingWatch.MarkSpent(_commitmentTxId, SpentAt, new Hash(block.GetHash().ToBytes()));
        _store.Watches[(_fundingWatch.TransactionId, _fundingWatch.OutputIndex)] = _fundingWatch;
        UseClose(ChannelCloseKind.RemoteCommitment, block);
    }

    [Fact]
    public async Task Given_FundingSpendReorgedOut_Then_StateNotLoweredAndOutpointRewatched()
    {
        // Arrange: our to_remote was swept at SpentAt + 1 (row Resolved); a reorg from SpentAt - 1 takes both blocks
        // out and the chain monitor rolled the watch spends back
        var executor = CreateExecutor(irrevocableDepth: 2);
        var sweepTxId = new TxId(Enumerable.Repeat((byte)0x5E, 32).ToArray());
        AddOutput(0, OutputDescriptorKind.PaymentToRemote, OutputResolutionState.Resolved, SpentAt + 1, sweepTxId);
        _chain.Mine();
        _chain.Reorg(SpentAt - 1, 4);
        var channelStatesBefore = _store.PersistedChannelStates.Count;

        // Act: rounds on the new branch (depth 2 would have made the old resolution irrevocable and closed the
        // channel)
        await executor.RunRoundAsync(SpentAt + 2, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(SpentAt + 3, TestContext.Current.CancellationToken);

        // Assert: still resolving on chain (never lowered, never Closed), the funding outpoint followed again, the
        // resolver not asked while the spend is off the chain, the resolution undone (our sweep pending again)
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
        Assert.Equal(channelStatesBefore, _store.PersistedChannelStates.Count);
        _memory.Verify(m => m.TryRemoveChannel(It.IsAny<ChannelId>()), Times.Never);
        _outpointWatcher.Verify(w => w.TrackWatchedOutpoint(_fundingWatch), Times.Once);
        Assert.Null(_resolver.LastHeight);
        var row = _store.Outputs[(_commitmentTxId, 0)];
        Assert.Equal(OutputResolutionState.Broadcast, row.State);
        Assert.Null(row.ResolvedHeight);
        Assert.Equal(sweepTxId, row.ResolvingTransactionId);

        // Act: the same commitment confirms again on the new branch (the watcher records its new block)
        var reconfirmed = _chain.Mine();
        UseClose(ChannelCloseKind.RemoteCommitment, reconfirmed, _chain.TipHeight);
        await executor.RunRoundAsync(_chain.TipHeight, TestContext.Current.CancellationToken);

        // Assert: resolved from its new block
        Assert.Equal(_chain.TipHeight, _resolver.LastHeight);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    [Fact]
    public async Task Given_RemoteCommitGoneAfterGrace_Then_LocalCommitBroadcast()
    {
        // Arrange: a signed state (an HTLC committed both ways); the peer's commitment is reorged out and does not
        // come back
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        var executor = CreateExecutor();
        _chain.Reorg(SpentAt - 1, 2);
        var tip = _chain.TipHeight;

        // Act: rounds during the grace period
        for (var height = tip; height < tip + Grace; height++)
            await executor.RunRoundAsync(height, TestContext.Current.CancellationToken);

        // Assert: nothing of ours yet
        Assert.DoesNotContain(_store.Broadcasts, b => b.Purpose == BroadcastPurpose.LocalCommitment);

        // Act: the grace is over
        await executor.RunRoundAsync(tip + Grace, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(tip + Grace + 1, TestContext.Current.CancellationToken);

        // Assert: our latest local commitment, signed, stored (with its number, for S1) and published once
        var ours = Assert.Single(_store.Broadcasts, b => b.Purpose == BroadcastPurpose.LocalCommitment);
        Assert.Equal(_pair.Alice.State.LocalCommit.Number, ours.CommitmentNumber);
        Assert.Equal(BroadcastState.Pending, ours.State);
        Assert.Equal(ours.TransactionId, Assert.Single(_published).TransactionId);
        var tx = Transaction.Load(ours.RawTransaction, Network.RegTest);
        Assert.Equal(new OutPoint(new uint256((byte[])_channel.FundingOutput!.TransactionId!.Value),
                                  _channel.FundingOutput.Index!.Value), Assert.Single(tx.Inputs).PrevOut);
        Assert.Equal(4, tx.Inputs[0].WitScript.PushCount);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    [Fact]
    public async Task Given_OurCommitmentReorgedOut_When_Round_Then_ItsAbandonedRowIsPendingAndPublishedAgain()
    {
        // Arrange: our commitment was the funding spend; its row was abandoned by a conflicting spend we saw earlier
        var executor = CreateExecutor();
        var row = new BroadcastTransactionModel(new SignedTransaction(_commitmentTxId, [1, 2, 3]),
                                                BroadcastPurpose.LocalCommitment, _channel.ChannelId, SpentAt - 1,
                                                commitmentNumber: _pair.Alice.State.LocalCommit.Number);
        row.MarkAbandoned();
        _store.Broadcasts.Add(row);
        UseClose(ChannelCloseKind.LocalCommitment, _chain[SpentAt]);
        _chain.Reorg(SpentAt - 1, 2);

        // Act
        await executor.RunRoundAsync(_chain.TipHeight, TestContext.Current.CancellationToken);

        // Assert: at once, no grace for our own commitment
        Assert.Equal(BroadcastState.Pending, row.State);
        Assert.Equal(_commitmentTxId, Assert.Single(_published).TransactionId);
    }

    [Fact]
    public async Task Given_ResolvedSpendRolledBackWhileTheCommitmentStays_When_Round_Then_UnresolvedAndNotAged()
    {
        // Arrange: only the block of our sweep is reorged out (the commitment stays); depth 2 would age it
        var executor = CreateExecutor(irrevocableDepth: 2);
        AddOutput(0, OutputDescriptorKind.PaymentToRemote, OutputResolutionState.Resolved, SpentAt + 1, null);
        _chain.Mine();
        _chain.Reorg(SpentAt, 3);

        // Act
        await executor.RunRoundAsync(_chain.TipHeight, TestContext.Current.CancellationToken);

        // Assert: the resolver runs (the commitment is on chain) and the row is pending again, never irrevocable
        Assert.Equal(_chain.TipHeight, _resolver.LastHeight);
        var output = _store.Outputs[(_commitmentTxId, 0)];
        Assert.Equal(OutputResolutionState.Pending, output.State);
        Assert.Null(output.ResolvedHeight);
        Assert.Equal(ChannelState.OnchainResolving, _channel.State);
    }

    [Fact]
    public async Task Given_FundingReconfirmedAtAnotherPosition_When_Handled_Then_ShortChannelIdMoves()
    {
        // Arrange (NL-292): an open channel whose funding transaction was confirmed at 500x3
        var open = _pair.Bob.Channel;
        open.ShortChannelId = new ShortChannelId(500, 3, open.FundingOutput!.Index!.Value);
        open.FundingCreatedAtBlockHeight = 500;
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
               {
                   channel = id == open.ChannelId ? open : null;
                   return channel is not null;
               }));
        var stored = new List<ShortChannelId>();
        var unitOfWork = new Mock<IUnitOfWork>();
        var channels = new Mock<IChannelDbRepository>();
        channels.Setup(r => r.GetByIdAsync(open.ChannelId)).ReturnsAsync(open);
        channels.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                .Callback<ChannelModel>(c => stored.Add(c.ShortChannelId))
                .Returns(Task.CompletedTask);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channels.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        using var provider = services.BuildServiceProvider();
        var handler = new FundingReconfirmationHandler(new ChannelLockProvider(), memory.Object,
                                                       NullLogger.Instance,
                                                       provider.GetRequiredService<IServiceScopeFactory>());
        var watch = new WatchedTransactionModel(open.ChannelId, open.FundingOutput.TransactionId!.Value, 3);
        watch.SetHeightAndIndex(501, 7);

        // Act
        var moved = await handler.HandleAsync(watch, TestContext.Current.CancellationToken);
        var again = await handler.HandleAsync(watch, TestContext.Current.CancellationToken);

        // Assert: the new position is saved, then applied; the same confirmation again changes nothing
        Assert.True(moved);
        Assert.False(again);
        var expected = new ShortChannelId(501, 7, open.FundingOutput.Index.Value);
        Assert.Equal([expected], stored);
        Assert.Equal(expected, open.ShortChannelId);
        Assert.Equal(501u, open.FundingCreatedAtBlockHeight);
        unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_FirstFundingConfirmation_When_Handled_Then_LeftToTheChannelManager()
    {
        // Arrange: no short channel id yet
        var open = _pair.Bob.Channel;
        open.ShortChannelId = default;
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
               {
                   channel = open;
                   return true;
               }));
        var handler = new FundingReconfirmationHandler(new ChannelLockProvider(), memory.Object,
                                                       NullLogger.Instance,
                                                       _provider.GetRequiredService<IServiceScopeFactory>());
        var watch = new WatchedTransactionModel(open.ChannelId, open.FundingOutput!.TransactionId!.Value, 3);
        watch.SetHeightAndIndex(501, 7);

        // Act
        var moved = await handler.HandleAsync(watch, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(moved);
        Assert.Equal(default, open.ShortChannelId);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private OnchainResolutionExecutor CreateExecutor(uint irrevocableDepth = 100) =>
        new(_broadcaster.Object, new ChannelLockProvider(), _memory.Object,
            NullLogger<OnchainResolutionExecutor>.Instance, _outpointWatcher.Object,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OnchainOptions { IrrevocableDepth = irrevocableDepth, ReorgGraceBlocks = Grace }));

    private void UseClose(ChannelCloseKind kind, Block block, uint height = SpentAt) =>
        _store.Closes[_channel.ChannelId] = new ChannelCloseModel(_channel.ChannelId, kind, _commitmentTxId,
                                                                  _pair.Alice.State.RemoteCommit.Number, height,
                                                                  new Hash(block.GetHash().ToBytes()),
                                                                  DateTimeOffset.UtcNow);

    /// <summary>An output row with its watch; a resolved row's spend was rolled back by the chain monitor.</summary>
    private void AddOutput(uint vout, OutputDescriptorKind kind, OutputResolutionState state, uint? resolvedHeight,
                           TxId? resolvingTxId)
    {
        _store.Outputs[(_commitmentTxId, vout)] = new OutputResolutionModel
        {
            TransactionId = _commitmentTxId,
            OutputIndex = vout,
            ChannelId = _channel.ChannelId,
            Descriptor = kind,
            State = state,
            ResolvedHeight = resolvedHeight,
            ResolvingTransactionId = resolvingTxId
        };
        _store.Watches[(_commitmentTxId, vout)] = new WatchedOutpointModel(_commitmentTxId, vout, _channel.ChannelId,
                                                                           WatchedOutpointPurpose.ResolutionOutput);
    }

    /// <summary>A resolver that only records the rounds it was asked for.</summary>
    private sealed class RecordingResolver : IOutputResolver
    {
        public uint? LastHeight { get; private set; }

        public bool CanResolve(ChannelCloseKind kind) => true;

        public Task<IReadOnlyList<OutputResolverAction>> ResolveAsync(ChannelCloseModel close,
                                                                      IReadOnlyList<OutputResolutionModel> outputs,
                                                                      uint height,
                                                                      CancellationToken cancellationToken)
        {
            LastHeight = height;
            return Task.FromResult<IReadOnlyList<OutputResolverAction>>([]);
        }

        public Task<IReadOnlyList<OutputResolverAction>> OnOutputSpentAsync(
            ChannelCloseModel close, OutputResolutionModel output, ChainTx spendingTransaction, uint height,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OutputResolverAction>>([]);
    }
}