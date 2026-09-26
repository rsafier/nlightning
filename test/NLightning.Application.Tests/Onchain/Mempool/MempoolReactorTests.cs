using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Mempool;

using Application.Channels.Safety.Interfaces;
using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Interfaces;
using Application.Onchain.Mempool;
using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Revoked;
using Application.Protocol.Factories;
using Channels.Services;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Serialization;
using Onchain;

/// <summary>
/// BOLT 5 plan O8 (NL-098): <see cref="MempoolReactor"/> over <b>real</b> commitments (Alice's view of a
/// <see cref="RealSigningCommitmentPair"/>, the watcher and the penalty resolver of the node): a preimage in an
/// unconfirmed claim is staged and fulfilled upstream once; a revoked commitment in the mempool gets its penalty stored
/// and published with nothing else recorded; the penalty is linked when that commitment confirms and abandoned when
/// another one does or when the commitment leaves the mempool.
/// </summary>
public sealed class MempoolReactorTests : IDisposable
{
    private const uint Tip = 400; // far below the HTLCs' cltv_expiry (600): one batched penalty
    private const uint SpendHeight = 600;

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: false);
    private readonly OnchainTestStore _store = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IBitcoinChainService> _chainService = new();
    private readonly Mock<IHtlcSwitch> _switch = new();
    private readonly Mock<IChannelStateDbRepository> _channelState = new();
    private readonly Mock<ISecretStorageServiceFactory> _shachainFactory = new();
    private readonly StubRevokedDataSource _dataSource = new();
    private readonly List<string> _calls = [];
    private readonly List<BroadcastTransactionModel> _published = [];
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;
    private readonly ulong _ourHtlcId;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public MempoolReactorTests()
    {
        // Arrange (shared): our HTLC to Bob (preimage 1) and Bob's HTLC to us (preimage 2), committed on both sides
        _ourHtlcId = _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1));
        _pair.Add(_pair.Bob, 30_000_000, RealSigningCommitmentPair.Preimage(2));
        _pair.Settle(_pair.Alice);
        _channel = _pair.Alice.Channel;
        _channel.UpdateCommitments(_pair.Alice.State);

        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(Tip);
        _monitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                .Callback<BroadcastTransactionModel>(b =>
                 {
                     _calls.Add("publish");
                     _published.Add(b);
                 })
                .ReturnsAsync(true);
        _switch.Setup(s => s.HandleAsync(It.IsAny<IChannelDomainEvent>(), It.IsAny<CancellationToken>()))
               .Callback(() => _calls.Add("switch"))
               .Returns(Task.CompletedTask);
        _channelState.Setup(r => r.ApplyAsync(It.IsAny<ChannelCommitments>(), It.IsAny<ChannelTransition>()))
                     .Callback(() => _calls.Add("apply"))
                     .Returns(Task.CompletedTask);
        _store.OnSave = () => _calls.Add("save");

        var unitOfWork = _store.CreateUnitOfWork();
        unitOfWork.SetupGet(u => u.RemoteShachainDbRepository).Returns(new Mock<IRemoteShachainDbRepository>().Object);
        unitOfWork.SetupGet(u => u.ChannelStateDbRepository).Returns(_channelState.Object);
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new Domain.Node.Options.NodeOptions()));
        services.AddSingleton(Options.Create(new OnchainOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSerializationInfrastructureServices();
        services.AddSingleton(_pair.Alice.Signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddSingleton(_monitor.Object);
        services.AddSingleton(_chainService.Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton(new Mock<IChannelErrorSender>().Object);
        services.AddSingleton(new Mock<IOnchainResolutionExecutor>().Object);
        services.AddSingleton<IOutpointWatcher>(_monitor.Object); // as in production: the monitor also publishes
        services.AddSingleton(_shachainFactory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton<IMessageFactory, MessageFactory>();
        services.AddSingleton(_switch.Object);
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton<OnchainChannelWatcher>();
        services.AddSingleton<IRevokedCommitDataSource>(_dataSource);
        services.AddSingleton<RevokedCommitResolver>();
        services.AddOnchainMempoolServices();
        _provider = services.BuildServiceProvider();
    }

    private MempoolReactor Reactor => _provider.GetRequiredService<MempoolReactor>();

    [Fact]
    public async Task Given_PreimageOfOurHtlcInAnUnconfirmedClaim_When_Seen_Then_PersistedThenFulfilledUpstreamOnce()
    {
        // Arrange: Bob claims our offered HTLC with its preimage (and, in another input, reveals his own HTLC's
        // preimage, which is not ours to act on)
        var claim = Claim(RealSigningCommitmentPair.Preimage(1), RealSigningCommitmentPair.Preimage(2));

        // Act
        var first = await Reactor.HandleSpendAsync(claim, TestContext.Current.CancellationToken);
        var again = await Reactor.HandleSpendAsync(claim, TestContext.Current.CancellationToken);

        // Assert: the preimage is on our HTLC's record (saved) before the switch fulfills upstream, and only once
        Assert.Equal([_ourHtlcId], first.FulfilledHtlcIds);
        Assert.Empty(again.FulfilledHtlcIds);
        Assert.Equal(["apply", "save", "switch"], _calls);
        var record = _channel.Commitments!.GetHtlc(HtlcDirection.Outgoing, _ourHtlcId)!;
        Assert.Equal(RealSigningCommitmentPair.Preimage(1), record.KnownPreimage);
        _switch.Verify(s => s.HandleAsync(It.Is<OutgoingHtlcFulfilled>(f => f.HtlcId == _ourHtlcId
                                                                          && f.PaymentPreimage
                                                                          == RealSigningCommitmentPair.Preimage(1)),
                                          It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(_channel.Commitments.GetHtlc(HtlcDirection.Incoming, 0)!.KnownPreimage);
        Assert.Empty(_store.Closes);
        Assert.Equal(ChannelState.Open, _channel.State);
    }

    [Fact]
    public async Task Given_WitnessWithoutAPreimageOfOurs_When_Seen_Then_NothingIsWrittenOrRaised()
    {
        // Arrange: a 32-byte item that hashes to no HTLC of ours
        var claim = Claim(RealSigningCommitmentPair.Preimage(9));

        // Act
        var reaction = await Reactor.HandleSpendAsync(claim, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(reaction.FulfilledHtlcIds);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Given_RevokedCommitmentInTheMempool_When_Seen_Then_PenaltyStoredAndPublishedAndNothingElseRecorded()
    {
        // Arrange
        var revoked = PrepareBreach();

        // Act
        var reaction = await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);

        // Assert: one penalty spending every output of the revoked commitment, stored before it is published; no
        // close, no row, the channel still Open (the mempool is never a confirmation)
        Assert.Equal(FundingSpendKind.Revoked, reaction.FundingSpendKind);
        var penalty = Assert.Single(_store.Broadcasts);
        Assert.Equal(BroadcastPurpose.Penalty, penalty.Purpose);
        Assert.Equal(BroadcastState.Pending, penalty.State);
        Assert.Equal([penalty.TransactionId], reaction.PenaltyTransactionIds);
        Assert.Equal(["save", "publish"], _calls);
        var transaction = Transaction.Load(penalty.RawTransaction, Network.RegTest);
        var commitment = Transaction.Load(revoked.RawTxBytes, Network.RegTest);
        Assert.Equal(commitment.Outputs.Count, transaction.Inputs.Count);
        Assert.All(transaction.Inputs, i => Assert.Equal(commitment.GetHash(), i.PrevOut.Hash));
        Assert.Empty(_store.Closes);
        Assert.Empty(_store.Outputs);
        Assert.Equal(ChannelState.Open, _channel.State);
        Assert.Contains(revoked.TxId, Reactor.PreparedCommitments);
    }

    [Fact]
    public async Task Given_PenaltyAlreadyPrepared_When_TheCommitmentIsSeenAgain_Then_OnlyPublishedAgain()
    {
        // Arrange: seen once (e.g. announced again after a restart of the monitor)
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);

        // Act
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_store.Broadcasts);
        Assert.Single(_store.Saves);
        Assert.Equal(2, _published.Count);
        Assert.Equal(_published[0].TransactionId, _published[1].TransactionId);
    }

    [Fact]
    public async Task Given_PreparedPenalty_When_TheRevokedCommitmentConfirms_Then_TheRowsNameItAndNothingIsRebuilt()
    {
        // Arrange
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);
        var penalty = Assert.Single(_store.Broadcasts);

        // Act: the block holds the commitment
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(revoked), TestContext.Current.CancellationToken);
        await Reactor.CheckPreparedAsync(TestContext.Current.CancellationToken);

        // Assert: every row of the revoked commitment names the penalty, which stays pending (it confirms on its own)
        Assert.Equal(ChannelCloseKind.RevokedCommitment, outcome!.Kind);
        Assert.NotEmpty(_store.Outputs);
        Assert.All(_store.Outputs.Values, o =>
        {
            Assert.Equal(OutputResolutionState.Broadcast, o.State);
            Assert.Equal(penalty.TransactionId, o.ResolvingTransactionId);
        });
        Assert.Contains(_store.Outputs.Values, o => o is
        {
            Descriptor: OutputDescriptorKind.RevokedToLocal,
            DeadlineHeight: > SpendHeight
        });
        Assert.Equal(BroadcastState.Pending, penalty.State);
        Assert.Empty(Reactor.PreparedCommitments);
    }

    [Fact]
    public async Task Given_PreparedPenaltyMinedWithTheCommitment_When_TheCloseIsRecorded_Then_TheRowsStillNameIt()
    {
        // Arrange: the block holds both; the monitor marks the penalty confirmed before the watcher records the close
        // (found by the Docker proof: the resolver then built a second penalty that bitcoind refused)
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);
        var penalty = Assert.Single(_store.Broadcasts);
        penalty.MarkConfirmed(SpendHeight, OnchainTestStore.BlockHash(1));

        // Act
        await Watcher.HandleFundingSpentAsync(SpentBy(revoked), TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(_store.Outputs);
        Assert.All(_store.Outputs.Values, o => Assert.Equal(penalty.TransactionId, o.ResolvingTransactionId));
        Assert.Equal(BroadcastState.Confirmed, penalty.State);
    }

    [Fact]
    public async Task Given_PreparedPenalty_When_AnotherCommitmentConfirms_Then_ThePenaltyIsAbandoned()
    {
        // Arrange: the revoked commitment was replaced; Bob's current commitment wins the funding output instead
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);
        var penalty = Assert.Single(_store.Broadcasts);
        var remote = _pair.Alice.State.RemoteCommit;
        var current = BuildCommitment(CommitmentSide.Remote, remote.Spec, remote.Number, remote.PerCommitmentPoint);

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(current), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelCloseKind.RemoteCommitment, outcome!.Kind);
        Assert.Equal(BroadcastState.Abandoned, penalty.State);
        Assert.All(_store.Outputs.Values, o => Assert.Equal(OutputResolutionState.Pending, o.State));
    }

    [Fact]
    public async Task Given_PreparedPenalty_When_TheCommitmentLeavesTheMempool_Then_AbandonedAfterTheGraceBlocks()
    {
        // Arrange: bitcoind no longer knows the revoked commitment (evicted or replaced), except for one block
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);
        var penalty = Assert.Single(_store.Broadcasts);
        var answers = new Queue<bool>([false, true, false, false, false]);
        _chainService.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>()))
                     .ReturnsAsync(() => answers.Dequeue() ? Transaction.Load(revoked.RawTxBytes, Network.RegTest)
                                                           : null);
        var ct = TestContext.Current.CancellationToken;

        // Act / Assert: missing, back (the count restarts), then missing for EvictionGraceBlocks (3) blocks in a row
        await Reactor.CheckPreparedAsync(ct);
        await Reactor.CheckPreparedAsync(ct);
        await Reactor.CheckPreparedAsync(ct);
        await Reactor.CheckPreparedAsync(ct);
        Assert.Equal(BroadcastState.Pending, penalty.State);
        await Reactor.CheckPreparedAsync(ct);
        Assert.Equal(BroadcastState.Abandoned, penalty.State);
        Assert.Empty(Reactor.PreparedCommitments);
        Assert.Empty(_store.Closes);
    }

    [Fact]
    public async Task Given_PenaltyAbandonedAfterEviction_When_TheSameCommitmentConfirms_Then_PendingAgainLinkedAndPublished()
    {
        // Arrange: the revoked commitment left the mempool for the grace blocks, so its penalty was abandoned; then
        // the cheater got the same commitment mined. The penalty's txid is deterministic (lock time 0, the same unused
        // address, RFC 6979, the same fee), so a rebuilt one would match the abandoned row and never be sent.
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);
        var penalty = Assert.Single(_store.Broadcasts);
        await EvictAsync(penalty);
        _published.Clear();

        // Act
        var outcome = await Watcher.HandleFundingSpentAsync(SpentBy(revoked), TestContext.Current.CancellationToken);

        // Assert: pending again, every row names it (the resolver builds nothing), and it went out
        Assert.Equal(ChannelCloseKind.RevokedCommitment, outcome!.Kind);
        Assert.Equal(BroadcastState.Pending, penalty.State);
        Assert.NotEmpty(_store.Outputs);
        Assert.All(_store.Outputs.Values, o =>
        {
            Assert.Equal(OutputResolutionState.Broadcast, o.State);
            Assert.Equal(penalty.TransactionId, o.ResolvingTransactionId);
        });
        Assert.Equal([penalty.TransactionId], _published.Select(p => p.TransactionId));
        Assert.Equal(BroadcastState.Pending, _published[0].State);
    }

    [Fact]
    public async Task Given_PenaltyAbandonedAfterEviction_When_TheCommitmentIsSeenAgain_Then_PendingAgainAndPublished()
    {
        // Arrange
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);
        var penalty = Assert.Single(_store.Broadcasts);
        await EvictAsync(penalty);
        _published.Clear();
        _calls.Clear();

        // Act: the cheater broadcasts the same commitment again
        var reaction = await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);

        // Assert: the same row, pending again in one save before the publish, and followed again
        Assert.Single(_store.Broadcasts);
        Assert.Equal(BroadcastState.Pending, penalty.State);
        Assert.Equal([penalty.TransactionId], reaction.PenaltyTransactionIds);
        Assert.Equal(["save", "publish"], _calls);
        Assert.Equal(BroadcastState.Pending, _published.Single().State);
        Assert.Contains(revoked.TxId, Reactor.PreparedCommitments);
    }

    [Fact]
    public async Task Given_MonitorBehindBitcoindsTip_When_TheCommitmentIsUnknown_Then_NothingIsAbandoned()
    {
        // Arrange: without txindex bitcoind answers "unknown" for a confirmed transaction out of its mempool; while our
        // monitor catches up (behind the tip) or is halted the commitment may sit in a block not processed yet
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);
        var penalty = Assert.Single(_store.Broadcasts);
        _chainService.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>())).ReturnsAsync((Transaction?)null);
        _chainService.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(Tip + 10);
        var ct = TestContext.Current.CancellationToken;

        // Act: many blocks behind the tip, then halted at the tip
        for (var i = 0; i < 5; i++)
            await Reactor.CheckPreparedAsync(ct);
        _chainService.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(Tip);
        _monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        for (var i = 0; i < 5; i++)
            await Reactor.CheckPreparedAsync(ct);

        // Assert
        Assert.Equal(BroadcastState.Pending, penalty.State);
        Assert.Contains(revoked.TxId, Reactor.PreparedCommitments);

        // Act / Assert: at the tip and running, the grace blocks count again
        _monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(false);
        for (var i = 0; i < 3; i++)
            await Reactor.CheckPreparedAsync(ct);
        Assert.Equal(BroadcastState.Abandoned, penalty.State);
    }

    [Fact]
    public async Task Given_PenaltiesPreparedBeforeARestart_When_Restored_Then_TheirCommitmentIsFollowedAgain()
    {
        // Arrange
        var revoked = PrepareBreach();
        await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);
        var restarted = new MempoolReactor(_monitor.Object, new ChannelLockProvider(), _memory.Object,
                                           NullLogger<MempoolReactor>.Instance, Options.Create(new OnchainOptions()),
                                           _provider.GetRequiredService<IServiceScopeFactory>(), Watcher);

        // Act
        await restarted.RestorePreparedAsync();

        // Assert
        Assert.Equal([revoked.TxId], restarted.PreparedCommitments);
    }

    [Fact]
    public async Task Given_CloseAlreadyRecorded_When_TheCommitmentIsSeenInTheMempool_Then_NothingIsPrepared()
    {
        // Arrange: the block came first; the resolvers own the channel
        var revoked = PrepareBreach();
        await Watcher.HandleFundingSpentAsync(SpentBy(revoked), TestContext.Current.CancellationToken);
        var saves = _store.Saves.Count;

        // Act
        var reaction = await Reactor.HandleSpendAsync(FundingSpend(revoked), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(reaction.FundingSpendKind);
        Assert.Empty(_store.Broadcasts);
        Assert.Equal(saves, _store.Saves.Count);
    }

    [Fact]
    public async Task Given_OurOwnCommitmentInTheMempool_When_Seen_Then_OnlyClassified()
    {
        // Arrange: we force-closed; our own commitment is announced
        var local = _pair.Alice.State.LocalCommit;
        var ours = BuildCommitment(CommitmentSide.Local, local.Spec, local.Number, null);

        // Act
        var reaction = await Reactor.HandleSpendAsync(FundingSpend(ours), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(FundingSpendKind.LocalCommit, reaction.FundingSpendKind);
        Assert.Empty(reaction.PenaltyTransactionIds);
        Assert.Empty(_store.Broadcasts);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Given_StartedReactor_When_TheMonitorRaisesASpend_Then_ItIsHandledOnTheLoop()
    {
        // Arrange
        var reactor = Reactor;
        reactor.Start();

        // Act
        _monitor.Raise(m => m.OnWatchedOutpointSpentInMempool += null, _monitor.Object,
                       Claim(RealSigningCommitmentPair.Preimage(1)));
        await reactor.WhenIdleAsync();
        await reactor.StopAsync();

        // Assert
        _switch.Verify(s => s.HandleAsync(It.IsAny<OutgoingHtlcFulfilled>(), It.IsAny<CancellationToken>()),
                       Times.Once);
    }

    [Fact]
    public async Task Given_MempoolReactionDisabled_When_Started_Then_NothingIsSubscribed()
    {
        // Arrange
        var reactor = new MempoolReactor(_monitor.Object, new ChannelLockProvider(), _memory.Object,
                                         NullLogger<MempoolReactor>.Instance,
                                         Options.Create(new OnchainOptions { Mempool = { Enabled = false } }),
                                         _provider.GetRequiredService<IServiceScopeFactory>(), Watcher);

        // Act
        reactor.Start();
        _monitor.Raise(m => m.OnWatchedOutpointSpentInMempool += null, _monitor.Object,
                       Claim(RealSigningCommitmentPair.Preimage(1)));
        await reactor.WhenIdleAsync();
        await reactor.StopAsync();

        // Assert
        _switch.VerifyNoOtherCalls();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private OnchainChannelWatcher Watcher => _provider.GetRequiredService<OnchainChannelWatcher>();

    /// <summary>bitcoind forgets the commitment for the grace blocks (monitor at its tip): the penalty is abandoned.</summary>
    private async Task EvictAsync(BroadcastTransactionModel penalty)
    {
        _chainService.Setup(c => c.GetTransactionAsync(It.IsAny<uint256>())).ReturnsAsync((Transaction?)null);
        _chainService.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(Tip);
        for (var i = 0; i < new OnchainMempoolOptions().EvictionGraceBlocks; i++)
            await Reactor.CheckPreparedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(BroadcastState.Abandoned, penalty.State);
        Assert.Empty(Reactor.PreparedCommitments);
    }

    /// <summary>
    /// Bob's commitment with both HTLCs, revoked by the next round: its log entry and secret are known (as in the
    /// watcher's revoked case), and the penalty resolver's data source serves it.
    /// </summary>
    private SignedTransaction PrepareBreach()
    {
        var revoked = _pair.Alice.State.RemoteCommit;
        _pair.Add(_pair.Alice, 5_000_000, RealSigningCommitmentPair.Preimage(3));
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _store.RevocationLog[(_channel.ChannelId, revoked.Number)] =
            RevokedCommitmentModel.From(_channel.ChannelId, revoked);
        var secret = _pair.Bob.Signer.RevealPerCommitmentSecret(RealSigningCommitmentPair.ChannelId, revoked.Number);
        var shachain = new Mock<ISecretStorageService>();
        shachain.Setup(s => s.DeriveOldSecret(It.IsAny<ulong>())).Returns(secret);
        _shachainFactory.Setup(f => f.CreatePerCommitmentStorage()).Returns(shachain.Object);
        var spend = BuildCommitment(CommitmentSide.Remote, revoked.Spec, revoked.Number, revoked.PerCommitmentPoint);
        _dataSource.Context = new RevokedCommitContext(_channel, ChainTxMapper.FromTransaction(
                                                           Transaction.Load(spend.RawTxBytes, Network.RegTest)),
                                                       revoked.Number, secret, revoked.PerCommitmentPoint,
                                                       RevokedCommitmentModel.From(_channel.ChannelId, revoked), 0);
        return spend;
    }

    private MempoolSpendEventArgs FundingSpend(SignedTransaction spend) =>
        new(_channel.ChannelId, spend, _channel.FundingOutput!.TransactionId!.Value, _channel.FundingOutput.Index!.Value,
            false);

    private OutpointSpentEventArgs SpentBy(SignedTransaction spend) =>
        new(_channel.ChannelId, spend, SpendHeight, 1, _channel.FundingOutput!.TransactionId!.Value,
            _channel.FundingOutput.Index!.Value, OnchainTestStore.BlockHash(1));

    /// <summary>
    /// An unconfirmed transaction whose inputs claim HTLC outputs with <paramref name="preimages"/> (the
    /// <c>&lt;sig&gt; &lt;preimage&gt; &lt;script&gt;</c> witness of a direct preimage claim).
    /// </summary>
    private MempoolSpendEventArgs Claim(params Secret[] preimages)
    {
        var transaction = Network.RegTest.CreateTransaction();
        var parent = new uint256(Enumerable.Repeat((byte)0x31, 32).ToArray());
        for (var i = 0; i < preimages.Length; i++)
        {
            transaction.Inputs.Add(new OutPoint(parent, (uint)i));
            var signature = Enumerable.Repeat((byte)0x30, 71).ToArray();
            transaction.Inputs[i].WitScript = new WitScript([signature, (byte[])preimages[i], new byte[40]]);
        }

        transaction.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        var signed = new SignedTransaction(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());
        return new MempoolSpendEventArgs(_channel.ChannelId, signed, new TxId(parent.ToBytes()), 0, false);
    }

    /// <summary>A commitment of the channel as it would be on chain (unsigned: the txid is the same).</summary>
    private SignedTransaction BuildCommitment(CommitmentSide side, CommitmentSpec spec, ulong number,
                                              CompactPubKey? remotePoint)
    {
        var factory = _provider.GetRequiredService<ICommitmentTransactionModelFactory>();
        var builder = _provider.GetRequiredService<ICommitmentTransactionBuilder>();
        var txSpec = CommitmentTxSpec.FromCommitmentSpec(spec);
        var model = side == CommitmentSide.Local
                        ? factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Local, number)
                        : factory.CreateCommitmentTransactionModel(_channel, txSpec, CommitmentSide.Remote, number,
                                                                   remotePoint);
        return builder.BuildWithOutputMap(model).Transaction;
    }

    /// <summary>The penalty resolver's reads, served from the test (the revoked context, a destination, a feerate).
    /// </summary>
    private sealed class StubRevokedDataSource : IRevokedCommitDataSource
    {
        public RevokedCommitContext? Context { get; set; }

        public Task<RevokedCommitLoadResult> LoadAsync(ChannelCloseModel close, CancellationToken cancellationToken) =>
            Task.FromResult(Context is { } context
                                ? RevokedCommitLoadResult.Found(context)
                                : RevokedCommitLoadResult.Missing("no breach"));

        public Task<RevokedOutputSpend?> GetSpendAsync(TxId transactionId, uint outputIndex,
                                                       CancellationToken cancellationToken) =>
            Task.FromResult<RevokedOutputSpend?>(null);

        public Task<bool> IsOurTransactionAsync(TxId transactionId) => Task.FromResult(false);

        public Task<BroadcastTransactionModel?> GetBroadcastAsync(TxId transactionId) =>
            Task.FromResult<BroadcastTransactionModel?>(null);

        public Task<byte[]> GetDestinationScriptAsync(ChannelId channelId, CancellationToken cancellationToken) =>
            Task.FromResult(new Key(Enumerable.Repeat((byte)0x55, 32).ToArray()).PubKey.WitHash.ScriptPubKey
                                                                                 .ToBytes());

        public Task<uint> GetFeeratePerKwAsync(CancellationToken cancellationToken) => Task.FromResult(2_500u);
    }
}