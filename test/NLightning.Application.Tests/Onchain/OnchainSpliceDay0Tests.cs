using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain;

using Application.Channels.Services;
using Application.Onchain;
using Application.Onchain.Interfaces;
using Channels.Services;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Day-0 splice follow-ups on the resolution executor's block round (wave spr lane SPR-E): NL-492, a discarded splice's
/// wallet inputs are handed back only once the transaction that conflicts with it is irrevocable (and never while a
/// reorg can still bring the splice back); NL-493, the funding spends the chain monitor recorded before a crash are
/// handed to the watcher again once per process.
/// </summary>
public sealed class OnchainSpliceDay0Tests : IDisposable
{
    private const uint CloseHeight = 600;

    private static readonly TxId s_commitmentTxId = new(Enumerable.Repeat((byte)0xC6, 32).ToArray());
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0xC5, 32).ToArray());
    private static readonly TxId s_walletTxId = new(Enumerable.Repeat((byte)0xAA, 32).ToArray());

    private readonly RealSigningCommitmentPair _pair = new(false);
    private readonly Mock<IWatchedOutpointDbRepository> _watches = new();
    private readonly Mock<IChannelFundingDbRepository> _fundings = new();
    private readonly Mock<IInteractiveTxSessionDbRepository> _sessions = new();
    private readonly Mock<IInteractiveTxContributor> _contributor = new();
    private readonly Mock<IOnchainChannelWatcher> _watcher = new();
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IFeeInputSelector> _feeInputSelector = new();
    private readonly Mock<IOnchainResolutionDbRepository> _resolutions = new();
    private readonly List<FeeInputReservation> _reservations = [];
    private readonly List<InteractiveTxSessionModel> _storedSessions = [];
    private readonly ServiceProvider _provider;
    private readonly ChannelModel _channel;
    private readonly WatchedOutpointModel _fundingWatch;

    public OnchainSpliceDay0Tests()
    {
        _channel = _pair.Alice.Channel;
        var current = ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!;
        _fundingWatch = new WatchedOutpointModel(current.FundingTxId, current.OutputIndex, _channel.ChannelId,
                                                 WatchedOutpointPurpose.FundingOutput);
        _watches.Setup(w => w.GetAsync(current.FundingTxId, current.OutputIndex)).ReturnsAsync(_fundingWatch);
        _fundings.Setup(f => f.GetByChannelIdAsync(_channel.ChannelId))
                 .ReturnsAsync(() => [current, Splice(ChannelFundingStatus.Discarded)]);
        _sessions.Setup(s => s.GetByChannelIdAsync(_channel.ChannelId)).ReturnsAsync(() => _storedSessions.ToList());
        _sessions.Setup(s => s.GetByIdAsync(_channel.ChannelId, It.IsAny<Guid>()))
                 .ReturnsAsync((Domain.Channels.ValueObjects.ChannelId _, Guid id) =>
                                   _storedSessions.FirstOrDefault(s => s.SessionId == id));
        _sessions.Setup(s => s.UpdateAsync(It.IsAny<InteractiveTxSessionModel>()))
                 .Callback((InteractiveTxSessionModel updated) =>
                  {
                      var index = _storedSessions.FindIndex(s => s.SessionId == updated.SessionId);
                      _storedSessions[index] = updated;
                  })
                 .Returns(Task.CompletedTask);
        _contributor.Setup(c => c.ReleaseDiscardedAsync(It.IsAny<ConstructedInteractiveTx>(),
                                                        It.IsAny<IReadOnlyCollection<(TxId, uint)>>(),
                                                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(1);
        _unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(_watches.Object);
        _unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(_fundings.Object);
        _unitOfWork.SetupGet(u => u.InteractiveTxSessionDbRepository).Returns(_sessions.Object);
        _unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(_resolutions.Object);
        _resolutions.Setup(r => r.GetOutputsByChannelIdAsync(It.IsAny<Domain.Channels.ValueObjects.ChannelId>()))
                    .ReturnsAsync([]);
        _feeInputSelector.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>()))
                         .ReturnsAsync(() => _reservations.ToList());

        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddSingleton(_contributor.Object);
        services.AddSingleton(_watcher.Object);
        services.AddSingleton(_chain.Object);
        services.AddSingleton(_feeInputSelector.Object);
        _provider = services.BuildServiceProvider();
    }

    #region NL-492

    [Fact]
    public async Task Given_ADiscardedSpliceOfOurs_When_TheCloseIsNotIrrevocable_Then_ItsInputsStayReservedUntilItIs()
    {
        // Arrange: the peer's commitment spent the funding at 600, which discarded our signed splice
        _storedSessions.Add(SpliceSession());
        _fundingWatch.MarkSpent(s_commitmentTxId, CloseHeight, new Hash(new byte[32]));
        var executor = CreateExecutor();

        // Act: 99 blocks deep, then 100
        await executor.RunRoundAsync(CloseHeight + 98, TestContext.Current.CancellationToken);
        var releasedAt99 = CountReleases();
        await executor.RunRoundAsync(CloseHeight + 99, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(CloseHeight + 100, TestContext.Current.CancellationToken);

        // Assert: released once, at the irrevocable depth, after the negotiation was marked settled in a save
        Assert.Equal(0, releasedAt99);
        Assert.Equal(1, CountReleases());
        _contributor.Verify(c => c.ReleaseDiscardedAsync(It.Is<ConstructedInteractiveTx>(t => t.TxId == s_spliceTxId),
                                                         It.Is<IReadOnlyCollection<(TxId, uint)>>(k => k.Count == 0),
                                                         It.IsAny<CancellationToken>()));
        Assert.NotNull(Assert.Single(_storedSessions).ResolvedAt);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_TheCloseReorgedOut_When_BlocksPass_Then_NothingIsReleased()
    {
        // Arrange: the close's block was disconnected (the monitor cleared the spend): the splice may confirm yet
        _storedSessions.Add(SpliceSession());
        _fundingWatch.MarkSpent(s_commitmentTxId, CloseHeight, new Hash(new byte[32]));
        var executor = CreateExecutor();
        await executor.RunRoundAsync(CloseHeight + 50, TestContext.Current.CancellationToken);
        _fundingWatch.ClearSpend();

        // Act
        await executor.RunRoundAsync(CloseHeight + 150, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, CountReleases());
        Assert.Null(Assert.Single(_storedSessions).ResolvedAt);
    }

    [Fact]
    public async Task Given_TheDiscardedSpliceItselfSpentTheFunding_When_Deep_Then_NothingIsReleased()
    {
        // Arrange: the watcher sets it Pending again then; until that save the record names the splice
        _storedSessions.Add(SpliceSession());
        _fundingWatch.MarkSpent(s_spliceTxId, CloseHeight, new Hash(new byte[32]));

        // Act
        await CreateExecutor().RunRoundAsync(CloseHeight + 150, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, CountReleases());
    }

    [Fact]
    public async Task Given_ALockedRbfSiblingSpentTheFunding_When_Irrevocable_Then_ItsInputsAreKept()
    {
        // Arrange: another splice of ours won; it re-added one of the discarded attempt's wallet inputs
        var winnerTxId = new TxId(Enumerable.Repeat((byte)0xC7, 32).ToArray());
        _storedSessions.Add(SpliceSession());
        _storedSessions.Add(SpliceSession(winnerTxId) with { ResolvedAt = DateTimeOffset.UnixEpoch });
        _fundingWatch.MarkSpent(winnerTxId, CloseHeight, new Hash(new byte[32]));
        IReadOnlyCollection<(TxId, uint)>? kept = null;
        _contributor.Setup(c => c.ReleaseDiscardedAsync(It.IsAny<ConstructedInteractiveTx>(),
                                                        It.IsAny<IReadOnlyCollection<(TxId, uint)>>(),
                                                        It.IsAny<CancellationToken>()))
                    .Callback((ConstructedInteractiveTx _, IReadOnlyCollection<(TxId, uint)> k, CancellationToken _) =>
                                  kept = k)
                    .ReturnsAsync(0);

        // Act
        await CreateExecutor().RunRoundAsync(CloseHeight + 99, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, CountReleases());
        Assert.NotNull(kept);
        Assert.Contains((s_walletTxId, 1u), kept);
    }

    [Fact]
    public async Task Given_TheContributorKeptTheReservation_When_ALaterRoundFreesIt_Then_TheSessionIsSettledThen()
    {
        // Arrange: the first release keeps the reservation (a kept outpoint the wallet still holds)
        _storedSessions.Add(SpliceSession());
        _fundingWatch.MarkSpent(s_commitmentTxId, CloseHeight, new Hash(new byte[32]));
        _reservations.Add(new FeeInputReservation(Guid.NewGuid(), "itx:splice:x",
                                                  [new WalletInput(s_walletTxId, 1, LightningMoney.Satoshis(100_000),
                                                                   AddressType.P2Wpkh, new BitcoinScript(new byte[22]),
                                                                   272)],
                                                  LightningMoney.Zero, LightningMoney.Zero, null));
        var executor = CreateExecutor();

        // Act: still reserved after the first round; the wallet dropped the kept outpoint before the second
        await executor.RunRoundAsync(CloseHeight + 99, TestContext.Current.CancellationToken);
        var resolvedAfterFirst = Assert.Single(_storedSessions).ResolvedAt;
        _reservations.Clear();
        await executor.RunRoundAsync(CloseHeight + 100, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(CloseHeight + 101, TestContext.Current.CancellationToken);

        // Assert: tried again until nothing held our input, then settled once
        Assert.Null(resolvedAfterFirst);
        Assert.Equal(2, CountReleases());
        Assert.NotNull(Assert.Single(_storedSessions).ResolvedAt);
        _unitOfWork.Verify(u => u.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Given_APendingSplice_When_TheFundingIsSpentLongAgo_Then_NothingIsReleased()
    {
        // Arrange: not discarded (the lock or the watcher decides it), so never released here
        _fundings.Setup(f => f.GetByChannelIdAsync(_channel.ChannelId))
                 .ReturnsAsync(() => [ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!,
                                      Splice(ChannelFundingStatus.Pending)]);
        _storedSessions.Add(SpliceSession());
        _fundingWatch.MarkSpent(s_commitmentTxId, CloseHeight, new Hash(new byte[32]));

        // Act
        await CreateExecutor().RunRoundAsync(CloseHeight + 150, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, CountReleases());
    }

    #endregion

    #region NL-493

    [Fact]
    public async Task Given_ARecordedFundingSpendAfterACrash_When_TheFirstRoundsRun_Then_ItIsHandedToTheWatcherOnce()
    {
        // Arrange: the chain monitor recorded the commitment on the funding watch, the watcher's save never happened
        var (block, commitment) = BlockWithSpendOf(_fundingWatch);
        var commitmentTxId = new TxId(commitment.GetHash().ToBytes());
        _fundingWatch.MarkSpent(commitmentTxId, CloseHeight, new Hash(block.GetHash().ToBytes()));
        _chain.Setup(c => c.GetBlockAsync(CloseHeight)).ReturnsAsync(block);
        OutpointSpentEventArgs? handed = null;
        _watcher.Setup(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                      It.IsAny<CancellationToken>()))
                .Callback((OutpointSpentEventArgs args, CancellationToken _) => handed = args)
                .ReturnsAsync((FundingSpendOutcome?)null);
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(CloseHeight + 1, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(CloseHeight + 2, TestContext.Current.CancellationToken);

        // Assert
        _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(handed);
        Assert.Equal(commitmentTxId, handed.SpendingTransaction.TxId);
        Assert.Equal(commitment.ToBytes(), handed.SpendingTransaction.RawTxBytes);
        Assert.Equal(CloseHeight, handed.BlockHeight);
        Assert.Equal(1u, handed.TransactionIndex);
        Assert.Equal(_fundingWatch.TransactionId, handed.SpentTransactionId);
        Assert.Equal(_fundingWatch.OutputIndex, handed.SpentOutputIndex);
        Assert.True(executor.FundingSpendReplay.IsDone);
    }

    [Fact]
    public async Task Given_ASpendByTheChannelsOwnSplice_When_ReplayingOnAnOpenChannel_Then_TheWatcherIsNotAsked()
    {
        // Arrange: the funding was spent by a splice of the channel (the lock path handles it)
        _fundings.Setup(f => f.GetByChannelIdAsync(_channel.ChannelId))
                 .ReturnsAsync(() => [ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!,
                                      Splice(ChannelFundingStatus.Pending)]);
        _fundingWatch.MarkSpent(s_spliceTxId, CloseHeight, new Hash(new byte[32]));
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(CloseHeight + 1, TestContext.Current.CancellationToken);

        // Assert
        _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Never);
        _chain.Verify(c => c.GetBlockAsync(It.IsAny<uint>()), Times.Never);
        Assert.True(executor.FundingSpendReplay.IsDone);
    }

    [Fact]
    public async Task Given_TheBlockCannotBeRead_When_Replaying_Then_TheNextRoundTriesAgain()
    {
        // Arrange
        var (block, commitment) = BlockWithSpendOf(_fundingWatch);
        _fundingWatch.MarkSpent(new TxId(commitment.GetHash().ToBytes()), CloseHeight, new Hash(new byte[32]));
        _chain.SetupSequence(c => c.GetBlockAsync(CloseHeight))
              .ThrowsAsync(new HttpRequestException("bitcoind unreachable"))
              .ReturnsAsync(block);
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(CloseHeight + 1, TestContext.Current.CancellationToken);
        var doneAfterFailure = executor.FundingSpendReplay.IsDone;
        await executor.RunRoundAsync(CloseHeight + 2, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(doneAfterFailure);
        _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Given_AFailedChannelWhoseFundingAPendingSpliceSpent_When_Replaying_Then_TheWatcherGetsItOnce()
    {
        // Arrange: a crash after the monitor recorded the splice, before the watcher handed it to the broadcaster
        var (block, splice) = BlockWithSpendOf(_fundingWatch);
        var spliceTxId = new TxId(splice.GetHash().ToBytes());
        _channel.UpdateState(ChannelState.Failed);
        _fundings.Setup(f => f.GetByChannelIdAsync(_channel.ChannelId))
                 .ReturnsAsync(() => [ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!,
                                      Splice(ChannelFundingStatus.Pending, spliceTxId)]);
        _fundingWatch.MarkSpent(spliceTxId, CloseHeight, new Hash(block.GetHash().ToBytes()));
        _chain.Setup(c => c.GetBlockAsync(CloseHeight)).ReturnsAsync(block);
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(CloseHeight + 1, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(CloseHeight + 2, TestContext.Current.CancellationToken);

        // Assert
        _watcher.Verify(w => w.HandleFundingSpentAsync(
                            It.Is<OutpointSpentEventArgs>(a => a.SpendingTransaction.TxId == spliceTxId
                                                            && a.SpentTransactionId == _fundingWatch.TransactionId),
                            It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(executor.FundingSpendReplay.IsDone);
    }

    [Fact]
    public async Task Given_ADiscardedSpliceNextToARecordedClose_When_Replaying_Then_TheWatcherGetsTheSplice()
    {
        // Arrange: the close's save discarded the splice, then a reorg confirmed the splice instead; a crash hit
        // before the watcher retired the close
        var (block, splice) = BlockWithSpendOf(_fundingWatch);
        var spliceTxId = new TxId(splice.GetHash().ToBytes());
        _channel.UpdateState(ChannelState.OnchainResolving);
        _fundings.Setup(f => f.GetByChannelIdAsync(_channel.ChannelId))
                 .ReturnsAsync(() => [ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!,
                                      Splice(ChannelFundingStatus.Discarded, spliceTxId)]);
        _resolutions.Setup(r => r.GetCloseAsync(_channel.ChannelId))
                    .ReturnsAsync(new ChannelCloseModel(_channel.ChannelId, ChannelCloseKind.RemoteCommitment,
                                                        s_commitmentTxId, 0, CloseHeight - 5,
                                                        new Hash(new byte[32]), DateTimeOffset.UnixEpoch));
        _fundingWatch.MarkSpent(spliceTxId, CloseHeight, new Hash(block.GetHash().ToBytes()));
        _chain.Setup(c => c.GetBlockAsync(CloseHeight)).ReturnsAsync(block);
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(CloseHeight + 1, TestContext.Current.CancellationToken);

        // Assert
        _watcher.Verify(w => w.HandleFundingSpentAsync(
                            It.Is<OutpointSpentEventArgs>(a => a.SpendingTransaction.TxId == spliceTxId
                                                            && a.SpendingTransaction.RawTxBytes.SequenceEqual(
                                                                   splice.ToBytes())
                                                            && a.BlockHeight == CloseHeight
                                                            && a.TransactionIndex == 1u),
                            It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(executor.FundingSpendReplay.IsDone);
    }

    [Fact]
    public async Task Given_ARecordNamingATransactionNoLongerInItsBlock_When_Replaying_Then_NothingIsHandedOver()
    {
        // Arrange: the block at that height no longer holds the recorded spend (a stale record after a reorg)
        var (block, _) = BlockWithSpendOf(_fundingWatch);
        _fundingWatch.MarkSpent(s_commitmentTxId, CloseHeight, new Hash(new byte[32]));
        _chain.Setup(c => c.GetBlockAsync(CloseHeight)).ReturnsAsync(block);
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(CloseHeight + 1, TestContext.Current.CancellationToken);

        // Assert
        _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(executor.FundingSpendReplay.IsDone);
    }

    [Fact]
    public async Task Given_TheWatcherThrows_When_Replaying_Then_TheNextRoundHandsTheSpendOverAgain()
    {
        // Arrange
        var (block, commitment) = BlockWithSpendOf(_fundingWatch);
        _fundingWatch.MarkSpent(new TxId(commitment.GetHash().ToBytes()), CloseHeight,
                                new Hash(block.GetHash().ToBytes()));
        _chain.Setup(c => c.GetBlockAsync(CloseHeight)).ReturnsAsync(block);
        _watcher.SetupSequence(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                              It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("database unavailable"))
                .ReturnsAsync((FundingSpendOutcome?)null);
        var executor = CreateExecutor();

        // Act
        await executor.RunRoundAsync(CloseHeight + 1, TestContext.Current.CancellationToken);
        var doneAfterFailure = executor.FundingSpendReplay.IsDone;
        await executor.RunRoundAsync(CloseHeight + 2, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(CloseHeight + 3, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(doneAfterFailure);
        Assert.True(executor.FundingSpendReplay.IsDone);
        _watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                       It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_APrunedBlock_When_ReplayingEveryRound_Then_ItWarnsAndGivesUpAfterTheLimit()
    {
        // Arrange
        var (_, commitment) = BlockWithSpendOf(_fundingWatch);
        _fundingWatch.MarkSpent(new TxId(commitment.GetHash().ToBytes()), CloseHeight, new Hash(new byte[32]));
        _chain.Setup(c => c.GetBlockAsync(CloseHeight)).ReturnsAsync((Block?)null);
        var logger = new RecordingLogger<OnchainResolutionExecutor>();
        var executor = CreateExecutor(logger);

        // Act
        for (var round = 1u; round < Application.Onchain.Reorg.RecordedFundingSpendReplay.MaxRounds; round++)
            await executor.RunRoundAsync(CloseHeight + round, TestContext.Current.CancellationToken);
        var doneBeforeLimit = executor.FundingSpendReplay.IsDone;
        await executor.RunRoundAsync(CloseHeight + 500, TestContext.Current.CancellationToken);
        await executor.RunRoundAsync(CloseHeight + 501, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(doneBeforeLimit);
        Assert.True(executor.FundingSpendReplay.IsDone);
        Assert.Contains(logger.Entries, e => e is { Level: LogLevel.Warning } && e.Message.Contains("pruned"));
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Critical);
        _chain.Verify(c => c.GetBlockAsync(CloseHeight),
                      Times.Exactly(Application.Onchain.Reorg.RecordedFundingSpendReplay.MaxRounds));
    }

    [Fact]
    public async Task Given_AResolvingChannelWithoutARecordedClose_When_RoundsRun_Then_TheWarningIsLoggedOnce()
    {
        // Arrange: a close retired for its splice; the channel waits for a commitment on the splice funding
        _channel.UpdateState(ChannelState.OnchainResolving);
        var logger = new RecordingLogger<OnchainResolutionExecutor>();
        var executor = CreateExecutor(logger);

        // Act
        for (var round = 1u; round <= 3; round++)
            await executor.RunRoundAsync(CloseHeight + round, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(logger.Entries,
                      e => e.Level == LogLevel.Warning && e.Message.Contains("without a recorded funding spend"));
    }

    #endregion

    public void Dispose()
    {
        _provider.Dispose();
        _pair.Dispose();
    }

    private int CountReleases() =>
        _contributor.Invocations.Count(i => i.Method.Name == nameof(IInteractiveTxContributor.ReleaseDiscardedAsync));

    private OnchainResolutionExecutor CreateExecutor(ILogger<OnchainResolutionExecutor>? logger = null)
    {
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
              .Returns((Func<ChannelModel, bool> predicate) => new[] { _channel }.Where(predicate).ToList());
        var channel = _channel;
        memory.Setup(m => m.TryGetChannel(_channel.ChannelId, out channel)).Returns(true);
        return new OnchainResolutionExecutor(new Mock<IChainBroadcaster>().Object, new ChannelLockProvider(),
                                             memory.Object, logger ?? NullLogger<OnchainResolutionExecutor>.Instance,
                                             new Mock<IOutpointWatcher>().Object,
                                             _provider.GetRequiredService<IServiceScopeFactory>());
    }

    private ChannelFunding Splice(ChannelFundingStatus status, TxId? txId = null)
    {
        var current = ChannelFunding.FromFundingOutput(_channel.FundingOutput!)!;
        return current with
        {
            FundingTxId = txId ?? s_spliceTxId,
            OutputIndex = 0,
            Kind = ChannelFundingKind.Splice,
            Status = status
        };
    }

    /// <summary>Our splice negotiation: the shared input is the current funding, one wallet input of ours.</summary>
    private InteractiveTxSessionModel SpliceSession(TxId? txId = null)
    {
        var funding = _channel.FundingOutput!;
        var script = new BitcoinScript(new byte[22]);
        IReadOnlyList<InteractiveTxInput> inputs =
        [
            new(0, InteractiveTxParty.Local, funding.TransactionId!.Value, funding.Index!.Value, 0xFFFFFFFD,
                funding.Amount, script, null, true),
            new(2, InteractiveTxParty.Local, s_walletTxId, 1, 0xFFFFFFFD, LightningMoney.Satoshis(100_000), script,
                [0x00], false)
        ];
        IReadOnlyList<InteractiveTxOutput> outputs =
        [
            new(4, InteractiveTxParty.Local, funding.Amount + LightningMoney.Satoshis(99_000), script, true)
        ];
        return new InteractiveTxSessionModel
        {
            ChannelId = _channel.ChannelId,
            SessionId = Guid.NewGuid(),
            Purpose = InteractiveTxPurpose.Splice,
            IsInitiator = true,
            FeeratePerKw = 253,
            Locktime = 0,
            Inputs = inputs,
            Outputs = outputs,
            LocalContribution = InteractiveTxContribution.Empty,
            ConstructedTx = new ConstructedInteractiveTx(txId ?? s_spliceTxId, [0x02], 0, inputs, outputs, 1_000, 0),
            State = InteractiveTxSessionState.TxSignaturesSent,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    /// <summary>A block whose second transaction spends the watched funding outpoint.</summary>
    private static (Block Block, Transaction Spend) BlockWithSpendOf(WatchedOutpointModel watch)
    {
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        var coinbase = Network.RegTest.CreateTransaction();
        coinbase.Inputs.Add(new TxIn(new OutPoint(uint256.Zero, uint.MaxValue)));
        coinbase.Outputs.Add(new TxOut(Money.Coins(1), new Key().PubKey.WitHash.ScriptPubKey));
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])watch.TransactionId), watch.OutputIndex)));
        spend.Outputs.Add(new TxOut(Money.Satoshis(900_000), new Key().PubKey.WitHash.ScriptPubKey));
        block.Transactions.Add(coinbase);
        block.Transactions.Add(spend);
        return (block, spend);
    }
}