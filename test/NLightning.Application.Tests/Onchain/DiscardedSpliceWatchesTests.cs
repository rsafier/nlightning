using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Onchain;

using Application.Onchain.Reorg;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
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
/// NL-529, in the round that releases a losing attempt's reservations (NL-528): once the confirmed funding of a
/// dual-funded open is irrevocable, the funding watches of its losing RBF attempts go — the watched transaction (row
/// and the chain monitor's memory) and the watched funding outpoint (row and memory) — so neither is loaded again on
/// the next start. The reservation release itself is lane SPR-E's (NL-492's round), not re-proved here.
/// </summary>
public sealed class DiscardedSpliceReservationsTests
{
    private const uint IrrevocableDepth = 100;
    private const uint ConfirmedHeight = 600;

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x51, 32).ToArray());
    private static readonly TxId s_confirmedTxId = new(Enumerable.Repeat((byte)0x0A, 32).ToArray());
    private static readonly TxId s_losingTxId = new(Enumerable.Repeat((byte)0x0B, 32).ToArray());
    private static readonly TxId s_walletTxId = new(Enumerable.Repeat((byte)0xAA, 32).ToArray());

    private readonly Mock<IWatchedTransactionDbRepository> _transactionWatches = new();
    private readonly Mock<IWatchedOutpointDbRepository> _outpointWatches = new();
    private readonly Mock<IInteractiveTxSessionDbRepository> _sessions = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Dictionary<TxId, WatchedTransactionModel> _transactionRows = [];
    private readonly Dictionary<(TxId, uint), WatchedOutpointModel> _outpointRows = [];
    private readonly List<InteractiveTxSessionModel> _storedSessions = [];
    private readonly ServiceProvider _provider;
    private ChannelModel _channel;

    public DiscardedSpliceReservationsTests()
    {
        _channel = CreateConfirmedOpen();
        _transactionWatches.Setup(r => r.GetByTransactionIdAsync(It.IsAny<TxId>()))
                           .ReturnsAsync((TxId txId) => _transactionRows.GetValueOrDefault(txId));
        _transactionWatches.Setup(r => r.DeleteByTransactionIdAsync(It.IsAny<TxId>()))
                           .Returns((TxId txId) => Task.FromResult(_transactionRows.Remove(txId)));
        _outpointWatches.Setup(r => r.GetAsync(It.IsAny<TxId>(), It.IsAny<uint>()))
                        .ReturnsAsync((TxId txId, uint index) => _outpointRows.GetValueOrDefault((txId, index)));
        _outpointWatches.Setup(r => r.DeleteByTransactionIdAsync(It.IsAny<TxId>(), It.IsAny<uint>()))
                        .Returns((TxId txId, uint index) =>
                                     Task.FromResult(_outpointRows.Remove((txId, index))));
        _sessions.Setup(s => s.GetByChannelIdAsync(_channel.ChannelId))
                 .ReturnsAsync(() => _storedSessions.ToList());

        _unitOfWork.SetupGet(u => u.WatchedTransactionDbRepository).Returns(_transactionWatches.Object);
        _unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(_outpointWatches.Object);
        _unitOfWork.SetupGet(u => u.InteractiveTxSessionDbRepository).Returns(_sessions.Object);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddSingleton(_monitor.Object);
        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Given_AnOpenWhoseRbfLost_When_TheConfirmedFundingIsIrrevocable_Then_ItsWatchesGo()
    {
        // Arrange: the open's first attempt confirmed at 600; the RBF attempt that lost keeps both of its watches
        var round = CreateRound();
        _storedSessions.Add(WinnerSession());
        _storedSessions.Add(LosingSession(withOurInput: true));
        StageLosingWatches();

        // Act: one block short of the depth (Depth is tip - confirmed + 1), then the depth reached
        await round.CheckAsync(ConfirmedHeight + IrrevocableDepth - 2, TestContext.Current.CancellationToken);
        await round.CheckAsync(ConfirmedHeight + IrrevocableDepth - 1, TestContext.Current.CancellationToken);

        // Assert: the losing attempt's transaction watch and funding outpoint watch are gone, on disk and in memory
        Assert.Empty(_transactionRows);
        Assert.Empty(_outpointRows);
        _monitor.Verify(m => m.StopWatchingTransaction(s_losingTxId), Times.Once);
        _monitor.Verify(m => m.StopWatchingOutpointSpend(s_losingTxId, 0), Times.Once);
        // The winner's own watches are untouched
        Assert.DoesNotContain(_outpointWatches.Invocations,
                              i => i.Method.Name == nameof(IWatchedOutpointDbRepository.DeleteByTransactionIdAsync)
                                && (TxId)i.Arguments[0] == s_confirmedTxId);
    }

    [Fact]
    public async Task Given_AShorterDepth_When_Checked_Then_NothingIsRemovedYet()
    {
        // Arrange
        var round = CreateRound();
        _storedSessions.Add(WinnerSession());
        _storedSessions.Add(LosingSession(withOurInput: false));
        StageLosingWatches();

        // Act
        await round.CheckAsync(ConfirmedHeight + IrrevocableDepth - 2, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_transactionRows);
        Assert.Single(_outpointRows);
        _monitor.Verify(m => m.StopWatchingTransaction(It.IsAny<TxId>()), Times.Never);
        _monitor.Verify(m => m.StopWatchingOutpointSpend(It.IsAny<TxId>(), It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public async Task Given_AnAttemptWithoutOurInputs_When_TheConfirmedFundingIsIrrevocable_Then_ItsWatchesStillGo()
    {
        // Arrange: an attempt only the peer funded into still carries its funding watches
        var round = CreateRound();
        _storedSessions.Add(WinnerSession());
        _storedSessions.Add(LosingSession(withOurInput: false));
        StageLosingWatches();

        // Act
        await round.CheckAsync(ConfirmedHeight + IrrevocableDepth, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_transactionRows);
        Assert.Empty(_outpointRows);
        _monitor.Verify(m => m.StopWatchingTransaction(s_losingTxId), Times.Once);
    }

    [Fact]
    public async Task Given_AV1SpliceChannel_When_Checked_Then_TheOpenCleanupDoesNotTouchIt()
    {
        // Arrange: the losing watch removal is the dual-funded open's path; a v1 splice has no confirmed open, and a
        // splice RBF's sibling is the SpliceRbf purpose, so its rows stay (the splice discard round settles those)
        var round = CreateRound();
        _storedSessions.Add(SpliceSiblingSession());
        StageLosingWatches();
        MakeV1();

        // Act
        await round.CheckAsync(ConfirmedHeight + IrrevocableDepth, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_storedSessions);
        Assert.Single(_transactionRows);
        Assert.Single(_outpointRows);
        _monitor.Verify(m => m.StopWatchingTransaction(It.IsAny<TxId>()), Times.Never);
    }

    /// <summary>The channel downgraded to a v1 splice: it can never be a confirmed dual-funded open.</summary>
    private void MakeV1()
    {
        var c = _channel;
        _channel = new ChannelModel(c.ChannelParams, c.ChannelId, c.CommitmentNumber, c.FundingOutput, c.IsInitiator,
                                    null, null, c.LocalBalance, c.LocalKeySet, 0, 0, c.RemoteBalance, c.RemoteKeySet,
                                    0, c.RemoteNodeId, 0, c.State, ChannelVersion.V1)
        {
            FundingCreatedAtBlockHeight = ConfirmedHeight
        };
    }

    private DiscardedSpliceReservations CreateRound()
    {
        var channels = new Mock<IChannelMemoryRepository>();
        channels.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                .Returns((Func<ChannelModel, bool> keep) => new[] { _channel }.Where(keep).ToList());
        var locks = new Mock<IChannelLockProvider>();
        locks.Setup(l => l.AcquireAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new OpenLock());

        return new DiscardedSpliceReservations(locks.Object, channels.Object, NullLogger.Instance,
                                               _provider.GetRequiredService<IServiceScopeFactory>(),
                                               IrrevocableDepth);
    }

    private void StageLosingWatches()
    {
        _transactionRows[s_losingTxId] = new WatchedTransactionModel(s_channelId, s_losingTxId, 1);
        _outpointRows[(s_losingTxId, 0)] =
            new WatchedOutpointModel(s_losingTxId, 0, s_channelId, WatchedOutpointPurpose.FundingOutput);
    }

    /// <summary>A v2 open whose first attempt confirmed (NL-528): the channel followed it, so it is the funding
    /// output, confirmed 100 blocks ago.</summary>
    private ChannelModel CreateConfirmedOpen()
    {
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(1_000_000),
                                     144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), Point(0x02), Point(0x03))
        {
            TransactionId = s_confirmedTxId,
            Index = 0
        };
        var keySet = new ChannelKeySetModel(0, Point(0x01), Point(0x03), Point(0x04), Point(0x05), Point(0x06),
                                            Point(0x07));
        var remoteKeySet = ChannelKeySetModel.CreateForRemote(Point(0x02), Point(0x13), Point(0x14), Point(0x15),
                                                              Point(0x16), Point(0x20));
        return new ChannelModel(channelParams, s_channelId, null, fundingOutput, true, null, null,
                                LightningMoney.Satoshis(900_000), keySet, 0, 0, LightningMoney.Satoshis(100_000),
                                remoteKeySet, 0, Point(0x20), 0, ChannelState.Open, ChannelVersion.V2)
        {
            FundingCreatedAtBlockHeight = ConfirmedHeight
        };
    }

    /// <summary>A splice RBF sibling: the SpliceRbf purpose, not the open's DualFund ones.</summary>
    private InteractiveTxSessionModel SpliceSiblingSession() =>
        Session(s_losingTxId, InteractiveTxPurpose.SpliceRbf, withOurInput: false);

    /// <summary>The confirmed attempt's session (its tx is the channel's funding output).</summary>
    private InteractiveTxSessionModel WinnerSession() =>
        Session(s_confirmedTxId, InteractiveTxPurpose.DualFund, withOurInput: true);

    private InteractiveTxSessionModel LosingSession(bool withOurInput) =>
        Session(s_losingTxId, InteractiveTxPurpose.DualFundRbf, withOurInput);

    private InteractiveTxSessionModel Session(TxId txId, InteractiveTxPurpose purpose, bool withOurInput)
    {
        var script = new BitcoinScript(new byte[22]);
        var inputs = new List<InteractiveTxInput>
        {
            new(1, InteractiveTxParty.Remote, s_walletTxId, 0, 0xFFFFFFFD, LightningMoney.Satoshis(2_000_000),
                script, [0x00], false)
        };
        if (withOurInput)
            inputs.Add(new InteractiveTxInput(2, InteractiveTxParty.Local, s_walletTxId, 1, 0xFFFFFFFD,
                                              LightningMoney.Satoshis(100_000), script, [0x00], false));
        return new InteractiveTxSessionModel
        {
            ChannelId = s_channelId,
            SessionId = Guid.NewGuid(),
            Purpose = purpose,
            IsInitiator = false,
            FeeratePerKw = 253,
            Locktime = 0,
            Inputs = inputs,
            Outputs = [],
            LocalContribution = InteractiveTxContribution.Empty,
            ConstructedTx = new ConstructedInteractiveTx(txId, [0x02], 0, inputs, [], 1_000, 0),
            State = InteractiveTxSessionState.Signed,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
    }

    private static CompactPubKey Point(byte tag)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[32] = tag;
        return bytes;
    }

    private sealed class OpenLock : IDisposable
    {
        public void Dispose()
        {
        }
    }
}