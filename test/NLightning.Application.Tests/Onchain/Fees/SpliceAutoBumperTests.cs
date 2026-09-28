using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Onchain.Fees;

using Application.Channels.Splicing;
using Application.Channels.Splicing.Interfaces;
using Application.Onchain.Fees;
using Channels.Splicing;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// The splice auto-bump (wave SPR, SPR-T3, lane SPR-B): a pending splice of ours unconfirmed for
/// <c>Splice:AutoBumpAfterBlocks</c> blocks is bumped through <see cref="ISpliceService.BumpAsync(SpliceBumpRequest,
/// CancellationToken)"/> exactly once per interval, at the fee estimate but at least the IT-RBF-01 minimum, never past
/// <c>Splice:MaxRbfAttempts</c>, and not at all while off. The splice service is a mock (its RBF is lane SPR-A's).
/// </summary>
public class SpliceAutoBumperTests
{
    private const uint Interval = 3;
    private const uint BroadcastHeight = 100;
    private const uint SpliceFeerate = 2_500;

    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x51, 32).ToArray());
    private static readonly CompactPubKey s_key = new(Enumerable.Repeat((byte)0x02, 33).ToArray());
    private static readonly TxId s_spliceTxId = new(Enumerable.Repeat((byte)0x0A, 32).ToArray());
    private static readonly TxId s_rbfTxId = new(Enumerable.Repeat((byte)0x0B, 32).ToArray());

    private readonly Mock<IChannelMemoryRepository> _channels = new();
    private readonly Mock<ISpliceService> _spliceService = new();
    private readonly Mock<ISpliceStatePort> _statePort = new();
    private readonly Mock<IFeeService> _feeService = new();
    private readonly Mock<IBroadcastTransactionDbRepository> _broadcasts = new();
    private readonly List<SpliceBumpRequest> _bumps = [];
    private readonly Dictionary<TxId, BroadcastTransactionModel> _rows = [];
    private readonly ChannelModel _channel;
    private FundingSet _fundings;
    private SpliceNegotiationModel? _negotiation;
    private Func<SpliceBumpRequest, SpliceResult> _bumpOutcome;

    public SpliceAutoBumperTests()
    {
        _channel = SpliceLockTestChannels.Create(s_channelId, ChannelState.Open, new ShortChannelId(400, 1, 0));
        _fundings = new FundingSet(Funding(0x01, ChannelFundingKind.Initial, ChannelFundingStatus.Current, null, 0),
                                   [Splice(s_spliceTxId, null, 100_000_000)]);
        _rows[s_spliceTxId] = Row(s_spliceTxId, BroadcastHeight);
        _bumpOutcome = r => new SpliceResult(r.ChannelId, SpliceNegotiationState.Aborted,
                                             FailureReason: "tx_abort: not now");

        _channels.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                 .Returns((Func<ChannelModel, bool> predicate) => new[] { _channel }.Where(predicate).ToList());
        _statePort.Setup(p => p.GetFundings(_channel)).Returns(() => _fundings);
        _spliceService.Setup(s => s.GetNegotiation(s_channelId)).Returns(() => _negotiation);
        _spliceService.Setup(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync((SpliceBumpRequest r, CancellationToken _) =>
                      {
                          _bumps.Add(r);
                          return _bumpOutcome(r);
                      });
        _feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(LightningMoney.Satoshis(1_000));
        _broadcasts.Setup(b => b.GetByTransactionIdAsync(It.IsAny<TxId>()))
                   .ReturnsAsync((TxId txId) => _rows.GetValueOrDefault(txId));
    }

    [Fact]
    public async Task Given_NoAutoBumpConfigured_When_ASpliceWaitedLong_Then_NothingIsBumped()
    {
        // Arrange: Splice:AutoBumpAfterBlocks unset (the default)
        var bumper = CreateBumper(interval: null);

        // Act
        var results = await bumper.BumpStaleSplicesAsync(BroadcastHeight + 1_000, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(results);
        Assert.False(bumper.IsEnabled);
        Assert.Empty(_bumps);
        _channels.Verify(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()), Times.Never);
    }

    [Fact]
    public async Task Given_ASpliceRefusedEachTime_When_BlocksPass_Then_BumpedExactlyOncePerInterval()
    {
        // Arrange: every bump is refused by the peer, so the latest attempt stays the same
        var bumper = CreateBumper();
        var ct = TestContext.Current.CancellationToken;

        // Act: one round per block from the broadcast on, height 103 twice
        var bumpedAt = new List<uint>();
        foreach (var height in new uint[] { 100, 101, 102, 103, 103, 104, 105, 106, 107, 108, 109 })
        {
            var results = await bumper.BumpStaleSplicesAsync(height, ct);
            if (results.Count > 0)
                bumpedAt.Add(height);
        }

        // Assert: at the broadcast + 3, then every 3 blocks after the previous try, never twice at one height
        Assert.Equal(new uint[] { 103, 106, 109 }, bumpedAt);
        Assert.Equal(3, _bumps.Count);
        Assert.All(_bumps, b => Assert.Equal(new SpliceBumpRequest(s_channelId, 2_604), b));
    }

    [Fact]
    public async Task Given_ASuccessfulBump_When_TheNewAttemptWaits_Then_ItsOwnIntervalStartsAtItsBroadcast()
    {
        // Arrange: the bump at 103 adds an RBF attempt broadcast at 104 (the service's save)
        var bumper = CreateBumper();
        var ct = TestContext.Current.CancellationToken;
        _bumpOutcome = r =>
        {
            _fundings = new FundingSet(_fundings.Current, [.. _fundings.Pending, Splice(s_rbfTxId, s_spliceTxId, 99_000_000,
                                                                                         r.FeeratePerKw)]);
            _rows[s_rbfTxId] = Row(s_rbfTxId, 104);
            return new SpliceResult(r.ChannelId, SpliceNegotiationState.Signed, s_rbfTxId, 1_099_000);
        };

        // Act
        var bumpedAt = new List<uint>();
        foreach (var height in new uint[] { 103, 104, 105, 106, 107 })
        {
            if ((await bumper.BumpStaleSplicesAsync(height, ct)).Count > 0)
                bumpedAt.Add(height);
        }

        // Assert: the second bump replaces the RBF attempt (2,604 sat/kw) at its own minimum
        Assert.Equal(new uint[] { 103, 107 }, bumpedAt);
        Assert.Equal(2_604U, _bumps[0].FeeratePerKw);
        Assert.Equal(2_712U, _bumps[1].FeeratePerKw);
    }

    [Fact]
    public async Task Given_MaxRbfAttemptsReached_When_TheSpliceWaits_Then_NoMoreBumps()
    {
        // Arrange: one RBF attempt allowed; the first bump succeeds and adds it
        var bumper = CreateBumper(maxRbfAttempts: 1);
        var ct = TestContext.Current.CancellationToken;
        _bumpOutcome = r =>
        {
            _fundings = new FundingSet(_fundings.Current, [.. _fundings.Pending, Splice(s_rbfTxId, s_spliceTxId, 99_000_000,
                                                                                         r.FeeratePerKw)]);
            _rows[s_rbfTxId] = Row(s_rbfTxId, 103);
            return new SpliceResult(r.ChannelId, SpliceNegotiationState.Signed, s_rbfTxId);
        };

        // Act
        for (uint height = 103; height <= 130; height++)
            await bumper.BumpStaleSplicesAsync(height, ct);

        // Assert
        Assert.Single(_bumps);
    }

    [Fact]
    public async Task Given_TheEstimateAboveTheRbfMinimum_When_Bumped_Then_TheEstimateIsUsed()
    {
        // Arrange
        _feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(LightningMoney.Satoshis(5_000));
        var bumper = CreateBumper();

        // Act
        await bumper.BumpStaleSplicesAsync(BroadcastHeight + Interval, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(5_000U, Assert.Single(_bumps).FeeratePerKw);
    }

    [Fact]
    public async Task Given_TheNextFeerateAboveTheMaximum_When_Due_Then_NotBumped()
    {
        // Arrange
        _feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(LightningMoney.Satoshis(300_000));
        var bumper = CreateBumper();

        // Act
        var results = await bumper.BumpStaleSplicesAsync(BroadcastHeight + Interval,
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(results);
        Assert.Empty(_bumps);
    }

    [Fact]
    public async Task Given_ThePeersSplice_When_Due_Then_NotOursToBump()
    {
        // Arrange: we contributed nothing and did not initiate it
        _fundings = new FundingSet(_fundings.Current, [Splice(s_spliceTxId, null, 0)]);
        var bumper = CreateBumper();

        // Act
        await bumper.BumpStaleSplicesAsync(BroadcastHeight + 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_bumps);
    }

    [Fact]
    public async Task Given_OurSpliceOutWithoutADelta_When_OurNegotiationNamedIt_Then_Bumped()
    {
        // Arrange: no delta on our side, but the service's last (signed) negotiation was ours and named the splice
        _fundings = new FundingSet(_fundings.Current, [Splice(s_spliceTxId, null, 0)]);
        _negotiation = Negotiation(SpliceNegotiationState.Signed, s_spliceTxId);
        var bumper = CreateBumper();

        // Act
        await bumper.BumpStaleSplicesAsync(BroadcastHeight + Interval, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_bumps);
    }

    [Theory]
    [InlineData(SpliceNegotiationState.AwaitingQuiescence)]
    [InlineData(SpliceNegotiationState.Negotiating)]
    [InlineData(SpliceNegotiationState.CommitmentSigned)]
    public async Task Given_ANegotiationRunning_When_Due_Then_NotBumped(SpliceNegotiationState state)
    {
        // Arrange
        _negotiation = Negotiation(state, null);
        var bumper = CreateBumper();

        // Act
        await bumper.BumpStaleSplicesAsync(BroadcastHeight + Interval, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_bumps);
    }

    [Fact]
    public async Task Given_AnAttemptConfirmed_When_Due_Then_NotBumped()
    {
        // Arrange
        _fundings = new FundingSet(_fundings.Current,
                                   [Splice(s_spliceTxId, null, 100_000_000) with { ConfirmedHeight = 101 }]);
        var bumper = CreateBumper();

        // Act
        await bumper.BumpStaleSplicesAsync(BroadcastHeight + 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_bumps);
    }

    [Fact]
    public async Task Given_TheBroadcastRowConfirmed_When_Due_Then_NotBumped()
    {
        // Arrange: the chain monitor saw it in a block (the depth watch not reached yet)
        _rows[s_spliceTxId] = Row(s_spliceTxId, BroadcastHeight, BroadcastState.Confirmed);
        var bumper = CreateBumper();

        // Act
        await bumper.BumpStaleSplicesAsync(BroadcastHeight + 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_bumps);
    }

    [Fact]
    public async Task Given_NoBroadcastRow_When_BlocksPass_Then_TheIntervalCountsFromWhenTheBumperSawIt()
    {
        // Arrange
        _rows.Clear();
        var bumper = CreateBumper();
        var ct = TestContext.Current.CancellationToken;

        // Act
        await bumper.BumpStaleSplicesAsync(200, ct);
        await bumper.BumpStaleSplicesAsync(202, ct);
        var early = _bumps.Count;
        await bumper.BumpStaleSplicesAsync(203, ct);

        // Assert
        Assert.Equal(0, early);
        Assert.Single(_bumps);
    }

    [Fact]
    public async Task Given_TheServiceThrows_When_Bumping_Then_AnAbortedResultAndRetriedOneIntervalLater()
    {
        // Arrange: lane SPR-A's rules refuse (or a service without RBF)
        _spliceService.Setup(s => s.BumpAsync(It.IsAny<SpliceBumpRequest>(), It.IsAny<CancellationToken>()))
                      .Callback<SpliceBumpRequest, CancellationToken>((r, _) => _bumps.Add(r))
                      .ThrowsAsync(new InvalidOperationException("SPR: the peer is not connected"));
        var bumper = CreateBumper();
        var ct = TestContext.Current.CancellationToken;

        // Act
        var first = await bumper.BumpStaleSplicesAsync(103, ct);
        await bumper.BumpStaleSplicesAsync(104, ct);
        await bumper.BumpStaleSplicesAsync(105, ct);
        await bumper.BumpStaleSplicesAsync(106, ct);

        // Assert
        var result = Assert.Single(first);
        Assert.Equal(SpliceNegotiationState.Aborted, result.State);
        Assert.Equal("SPR: the peer is not connected", result.FailureReason);
        Assert.Equal(2, _bumps.Count);
    }

    [Fact]
    public async Task Given_AClosingChannel_When_Due_Then_NotBumped()
    {
        // Arrange
        _channel.UpdateState(ChannelState.ShuttingDown);
        var bumper = CreateBumper();

        // Act
        await bumper.BumpStaleSplicesAsync(BroadcastHeight + 10, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_bumps);
    }

    [Fact]
    public async Task Given_TheBumperStarted_When_BlocksArrive_Then_EachBlockRunsARound()
    {
        // Arrange
        var monitor = new Mock<IBlockchainMonitor>();
        var bumper = CreateBumper(monitor: monitor.Object);
        bumper.Start();

        // Act
        foreach (var height in new uint[] { 101, 102, 103 })
        {
            monitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(height, new byte[32]));
            await bumper.WhenIdleAsync();
        }

        await bumper.StopAsync();
        monitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(106, new byte[32]));
        await bumper.WhenIdleAsync();

        // Assert: bumped at 103; nothing after the stop
        Assert.Single(_bumps);
    }

    [Fact]
    public void Given_TheAutoBumpOff_When_Started_Then_NotSubscribed()
    {
        // Arrange
        var monitor = new Mock<IBlockchainMonitor>();
        var bumper = CreateBumper(interval: null, monitor: monitor.Object);

        // Act
        bumper.Start();

        // Assert
        monitor.VerifyAdd(m => m.OnNewBlockDetected += It.IsAny<EventHandler<NewBlockEventArgs>>(), Times.Never);
    }

    [Fact]
    public void Given_TheRegistration_When_Resolved_Then_OneBumperAsItselfAndTheInterface()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_channels.Object);
        services.AddSingleton(_spliceService.Object);
        services.AddSingleton(_statePort.Object);
        services.AddSingleton(_feeService.Object);
        services.AddSpliceAutoBumper();
        services.AddSpliceAutoBumper();

        // Act
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Same(provider.GetRequiredService<SpliceAutoBumper>(), provider.GetRequiredService<ISpliceAutoBumper>());
        Assert.Single(services, d => d.ServiceType == typeof(ISpliceAutoBumper));
        Assert.False(provider.GetRequiredService<SpliceAutoBumper>().IsEnabled);
    }

    private SpliceAutoBumper CreateBumper(uint? interval = Interval, int maxRbfAttempts = 8,
                                          IBlockchainMonitor? monitor = null)
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(_broadcasts.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        var provider = services.BuildServiceProvider();
        var options = Options.Create(new SpliceOptions
        {
            AutoBumpAfterBlocks = interval,
            MaxRbfAttempts = maxRbfAttempts
        });
        return new SpliceAutoBumper(_channels.Object, _spliceService.Object, _statePort.Object, _feeService.Object,
                                    provider, NullLogger<SpliceAutoBumper>.Instance, options, monitor);
    }

    private static ChannelFunding Funding(byte tag, ChannelFundingKind kind, ChannelFundingStatus status,
                                          uint? feerate, long localDeltaMsat) =>
        new(new TxId(Enumerable.Repeat(tag, 32).ToArray()), 0, 1_000_000, s_key, s_key, 0, localDeltaMsat, 0, kind,
            status, feerate);

    private static ChannelFunding Splice(TxId txId, TxId? rbfOf, long localDeltaMsat, uint feerate = SpliceFeerate) =>
        new(txId, 0, (ulong)(1_000_000 + localDeltaMsat / 1_000), s_key, s_key, 1, localDeltaMsat, 0,
            rbfOf is null ? ChannelFundingKind.Splice : ChannelFundingKind.SpliceRbf, ChannelFundingStatus.Pending,
            feerate, 0, rbfOf);

    private static BroadcastTransactionModel Row(TxId txId, uint firstBroadcastHeight,
                                                 BroadcastState state = BroadcastState.Pending) =>
        BroadcastTransactionModel.Restore(txId, [0x02], BroadcastPurpose.Funding, s_channelId, SpliceFeerate, null,
                                          firstBroadcastHeight, state,
                                          state == BroadcastState.Confirmed ? firstBroadcastHeight + 1 : null,
                                          state == BroadcastState.Confirmed ? (Hash?)new Hash(new byte[32]) : null,
                                          DateTimeOffset.UnixEpoch);

    private static SpliceNegotiationModel Negotiation(SpliceNegotiationState state, TxId? txId) =>
        new(s_channelId, true, -50_000, 0, SpliceFeerate, 0, s_key, 1, null, false, false, null, state, txId,
            DateTimeOffset.UnixEpoch);
}