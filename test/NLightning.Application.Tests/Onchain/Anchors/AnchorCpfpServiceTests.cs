using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Anchors;

using Application.Channels.Safety;
using Application.Channels.Services;
using Application.Onchain.Anchors;
using Application.Onchain.Resolvers.Local;
using Channels.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// BOLT 5 plan O7-T2 (B5-FAIL-06): <see cref="AnchorCpfpService"/> over a <b>real</b> anchor commitment of a
/// <see cref="RealSigningCommitmentPair"/> (Alice fails the channel with an HTLC in flight): the child spends our anchor
/// (script-executed) and wallet inputs, the package pays the estimate, an RBF replacement raises the fee over BIP 125's
/// minimum and replaces the old row, the reservation is released once the commitment confirmed or lost, and the anchors
/// are swept 16 blocks after the confirmation only when that pays for itself.
/// </summary>
public sealed class AnchorCpfpServiceTests : IDisposable
{
    private const uint HtlcExpiry = 600;

    private RealSigningCommitmentPair _pair = null!;
    private ChannelModel _channel = null!;
    private ServiceProvider _provider = null!;
    private readonly InMemoryBroadcasts _store = new();
    private readonly FakeAnchorWallet _wallet = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly List<BroadcastTransactionModel> _published = [];
    private readonly List<SignedTransaction> _sweeps = [];
    private readonly byte[] _walletScript = new Key(Enumerable.Repeat((byte)0x33, 32).ToArray())
                                            .PubKey.WitHash.ScriptPubKey.ToBytes();
    private readonly FakeAnchorChain _chain = new();
    private readonly List<uint> _estimateTargets = [];
    private readonly List<ServiceProvider> _restarted = [];
    private uint _estimate = 10_000;
    private bool _walletSigns = true;
    private bool _failNextSave;
    private Mock<IFeeService> _feeService = null!;
    private Mock<ISweepDestinationProvider> _destination = null!;
    private Mock<IUnitOfWork> _unitOfWork = null!;
    private ILightningSigner _signer = null!;

    public AnchorCpfpServiceTests()
    {
        Init(hasAnchors: true);
    }

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    private void Init(bool hasAnchors)
    {
        _pair = new RealSigningCommitmentPair(hasAnchors);
        _channel = _pair.Alice.Channel;
        _wallet.AddUtxo(0x51, 50_000);
        _wallet.AddUtxo(0x52, 30_000);
        _wallet.AddUtxo(0x53, 200_000);

        _memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? channel) =>
                {
                    channel = _channel;
                    return id == _channel.ChannelId;
                }));
        _memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => predicate(_channel) ? [_channel] : []);
        _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(500);
        _monitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                .Callback<BroadcastTransactionModel>(_published.Add)
                .ReturnsAsync(true);
        _monitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
                .Callback<SignedTransaction>(_sweeps.Add)
                .Returns(Task.CompletedTask);

        _feeService = new Mock<IFeeService>();
        _feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                   .Callback<uint, CancellationToken>((target, _) => _estimateTargets.Add(target))
                   .ReturnsAsync(() => LightningMoney.Satoshis(_estimate));
        _feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => LightningMoney.Satoshis(_estimate));
        _destination = new Mock<ISweepDestinationProvider>();
        _destination.Setup(d => d.GetDestinationScriptAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_walletScript);

        _unitOfWork = new Mock<IUnitOfWork>();
        _unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(_store);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            if (_failNextSave)
            {
                _failNextSave = false;
                throw new InvalidOperationException("database down");
            }

            _store.Saves++;
            return Task.CompletedTask;
        });

        _signer = WalletSigningProxy.Create(_pair.Alice.Signer,
                                            tx => _walletSigns
                                                      ? _wallet.SignWalletInputs(tx)
                                                      : throw new NotImplementedException());
        _provider = BuildProvider();
    }

    /// <summary>A node's service graph over the shared store, wallet and chain (a second call is a restart).</summary>
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton<IBitcoinChainService>(_chain);
        services.AddSingleton(_signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddSingleton(_monitor.Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton(_feeService.Object);
        services.AddSingleton(_destination.Object);
        services.AddSingleton<IAnchorFeeInputSource>(_wallet);
        services.AddSingleton(new SweepFeePolicy());
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddAnchorCpfpServices();
        return services.BuildServiceProvider();
    }

    private AnchorCpfpService Service => _provider.GetRequiredService<AnchorCpfpService>();

    [Fact]
    public async Task Given_CommitmentBelowTheEstimate_When_Round_Then_ChildSpendsAnchorAndWalletInputsAtPackageRate()
    {
        // Arrange
        var commitment = BroadcastCommitment();

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert: one pending child row, published, spending our anchor first
        var row = Assert.Single(_store.Children);
        Assert.Equal(BroadcastState.Pending, row.State);
        Assert.Equal(500u, row.FirstBroadcastHeight);
        Assert.Null(row.ReplacesTransactionId);
        Assert.Equal(row, Assert.Single(_published));

        var commitmentTx = Load(commitment);
        var child = Load(row);
        var anchorVout = FindOurAnchor(commitmentTx);
        Assert.Equal(new OutPoint(commitmentTx.GetHash(), anchorVout), child.Inputs[0].PrevOut);
        Assert.True(child.Inputs.Count >= 2);
        Assert.Single(child.Outputs);
        Assert.Equal(_walletScript, child.Outputs[0].ScriptPubKey.ToBytes());
        AnchorTx.AssertScriptsValid(child, commitmentTx, _wallet);

        // The package pays the estimate (and not much more)
        var package = PackageFeerate(commitmentTx, child);
        Assert.InRange(package, _estimate, _estimate + 50);
        Assert.Equal(child.Inputs.Count - 1, _wallet.Reserved(_channel.ChannelId).Count);
        Assert.Equal(0, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_CommitmentPayingTheEstimate_When_Round_Then_NoChildAndNothingReserved()
    {
        // Arrange: the commitment pays 2,500 sat/kw
        BroadcastCommitment();
        _estimate = 2_000;

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_store.Children);
        Assert.Empty(_published);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_PendingChild_When_EstimateRisesAndBumpIsDue_Then_ReplacementRaisesFeeAndReplacesOldRow()
    {
        // Arrange
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var first = Assert.Single(_store.Children);
        _estimate = 20_000;

        // Act: one block later it is not due yet (RbfIntervalBlocks 2), two blocks later it is
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);
        var afterOneBlock = _store.Children.Count;
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, afterOneBlock);
        Assert.Equal(2, _store.Children.Count);
        var replacement = _store.Children.Single(c => c.TransactionId != first.TransactionId);
        Assert.Equal(BroadcastState.Replaced, first.State);
        Assert.Equal(BroadcastState.Pending, replacement.State);
        Assert.Equal(first.TransactionId, replacement.ReplacesTransactionId);
        Assert.Equal(502u, replacement.FirstBroadcastHeight);

        var commitmentTx = Load(commitment);
        var oldChild = Load(first);
        var newChild = Load(replacement);
        Assert.Equal(oldChild.Inputs[0].PrevOut, newChild.Inputs[0].PrevOut);
        AnchorTx.AssertScriptsValid(newChild, commitmentTx, _wallet);

        // BIP 125 rules 3 and 4, and the new estimate for the package
        var oldFee = AnchorTx.ChildFee(oldChild, _wallet);
        var newFee = AnchorTx.ChildFee(newChild, _wallet);
        Assert.True(newFee >= oldFee * 5 / 4, $"{newFee} < 1.25 x {oldFee}");
        Assert.True(newFee >= oldFee + (ulong)newChild.GetVirtualSize(), $"{newFee} below the relay increment");
        Assert.True(PackageFeerate(commitmentTx, newChild) >= 20_000);
        Assert.Equal([first, replacement], _published);
    }

    [Fact]
    public async Task Given_CommitmentStillUnconfirmedPastItsDeadline_When_EstimateRises_Then_ChildStillReplaced()
    {
        // Arrange: the first child at the HTLC's expiry, the commitment still out of blocks
        BroadcastCommitment();
        await Service.RunOnceAsync(HtlcExpiry, TestContext.Current.CancellationToken);
        var first = Assert.Single(_store.Children);
        _estimate = 20_000;

        // Act
        await Service.RunOnceAsync(HtlcExpiry + 1, TestContext.Current.CancellationToken);
        var afterOneBlock = _store.Children.Count;
        await Service.RunOnceAsync(HtlcExpiry + 2, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, afterOneBlock);
        Assert.Equal(BroadcastState.Replaced, first.State);
        Assert.Equal(2, _store.Children.Count);
    }

    [Fact]
    public async Task Given_PendingChildPayingTheEstimate_When_BumpIsDue_Then_Kept()
    {
        // Arrange
        BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Act: same estimate, the package still pays it
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert
        var child = Assert.Single(_store.Children);
        Assert.Equal(BroadcastState.Pending, child.State);
        Assert.Single(_published);
    }

    [Fact]
    public async Task Given_CommitmentAndChildConfirmed_When_Round_Then_ReservationReleasedOnce()
    {
        // Arrange
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        commitment.MarkConfirmed(501, OnchainTestStore.BlockHash(1));
        child.MarkConfirmed(501, OnchainTestStore.BlockHash(1));

        // Act
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BroadcastState.Confirmed, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
        Assert.Single(_store.Children);
    }

    [Fact]
    public async Task Given_CommitmentConfirmedWithoutItsChild_When_AnchorUnspent_Then_ChildKeptPendingAndInputsReserved()
    {
        // Arrange: the commitment confirmed alone; its child is still valid (it spends a confirmed output) and may
        // confirm, so a funding transaction must not take its wallet inputs (it would conflict under BIP 125)
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        var reserved = _wallet.Reserved(_channel.ChannelId).ToList();
        commitment.MarkConfirmed(501, OnchainTestStore.BlockHash(1));
        _estimate = 50_000;

        // Act
        for (uint height = 501; height < 520; height++)
            await Service.RunOnceAsync(height, TestContext.Current.CancellationToken);

        // Assert: kept, never bumped, nothing released or swept
        Assert.Equal(BroadcastState.Pending, child.State);
        Assert.Single(_store.Children);
        Assert.Equal(0, _wallet.ReleaseCount);
        Assert.Equal(reserved, _wallet.Reserved(_channel.ChannelId));
        Assert.Empty(_sweeps);
    }

    [Fact]
    public async Task Given_CommitmentConfirmedAndAnchorSpentOnChain_When_MonitorCaughtUp_Then_ChildAbandonedAndReleased()
    {
        // Arrange: a replaced child (the monitor stops tracking it) confirmed; the pending one can never confirm
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        commitment.MarkConfirmed(501, OnchainTestStore.BlockHash(1));
        var commitmentTx = Load(commitment);
        _chain.Spent.Add(new OutPoint(commitmentTx.GetHash(), FindOurAnchor(commitmentTx)));
        _chain.Tip = 503;

        // Act: seen spent at 502 with bitcoind at 503: the monitor may not have processed that block yet
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);
        var stateAt502 = child.State;
        var releasesAt502 = _wallet.ReleaseCount;
        await Service.RunOnceAsync(503, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BroadcastState.Pending, stateAt502);
        Assert.Equal(0, releasesAt502);
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_CommitmentConfirmedAndChildNeverConfirms_When_WaitPassed_Then_ChildAbandonedAndReleased()
    {
        // Arrange: bitcoind is unreachable, so only the wait ends it
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        commitment.MarkConfirmed(501, OnchainTestStore.BlockHash(1));
        _chain.Throws = true;
        var wait = new AnchorCpfpOptions().ConfirmedCommitmentChildWaitBlocks;

        // Act
        await Service.RunOnceAsync(501 + wait - 1, TestContext.Current.CancellationToken);
        var releasedBefore = _wallet.ReleaseCount;
        await Service.RunOnceAsync(501 + wait, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, releasedBefore);
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_PendingChildBeforeARestart_When_BumpIsDue_Then_ReplacementReusesTheDurableReservation()
    {
        // Arrange: the port keeps its reservations across the restart (IAnchorFeeInputSource's contract)
        BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var first = Assert.Single(_store.Children);
        var reserved = _wallet.Reserved(_channel.ChannelId).ToList();
        var restarted = BuildProvider();
        _restarted.Add(restarted);
        var service = restarted.GetRequiredService<AnchorCpfpService>();
        _estimate = 20_000;

        // Act
        await service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert: the replacement spends the same wallet inputs (plus more if needed), nothing was released
        Assert.Equal(BroadcastState.Replaced, first.State);
        var replacement = Load(_store.Children.Single(c => c.State == BroadcastState.Pending));
        var spent = replacement.Inputs.Skip(1).Select(i => i.PrevOut).ToHashSet();
        Assert.All(reserved, r => Assert.Contains(new OutPoint(new uint256(r.TxId), r.OutputIndex), spent));
        Assert.Equal(0, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_FirstChildSaveFails_When_Round_Then_ReservationReleasedAndNothingPublished()
    {
        // Arrange
        BroadcastCommitment();
        _failNextSave = true;

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_published);
        Assert.Equal(1, _wallet.ReleaseCount);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_OnlyATrimmedHtlc_When_Round_Then_NoDeadlineTargetIsUsed()
    {
        // Arrange: a 500 sat HTLC has no output on Alice's commitment (dust limit 546 sat)
        BroadcastCommitment(htlcMsat: 500_000);

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new AnchorCpfpOptions().NoDeadlineConfTarget, _estimateTargets[0]);
    }

    [Fact]
    public async Task Given_AnUntrimmedHtlc_When_Round_Then_ItsDeadlineSetsTheTarget()
    {
        // Arrange
        BroadcastCommitment();

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        var expected = new SweepFeePolicy().GetConfirmationTarget(500, HtlcExpiry);
        Assert.NotEqual(new AnchorCpfpOptions().NoDeadlineConfTarget, expected);
        Assert.Equal(expected, _estimateTargets[0]);
    }

    [Fact]
    public async Task Given_PeersCommitmentWon_When_Round_Then_ChildAbandonedAndReservationReleased()
    {
        // Arrange: the watcher abandons our commitment row when another transaction spent the funding output
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        commitment.MarkAbandoned();

        // Act
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BroadcastState.Abandoned, Assert.Single(_store.Children).State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_WalletCannotSign_When_Round_Then_NoChildAndReservationReleased()
    {
        // Arrange: SignWalletTransaction is not implemented (before O7-T1)
        BroadcastCommitment();
        _walletSigns = false;

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_store.Children);
        Assert.Empty(_published);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_EmptyWallet_When_Round_Then_NoChild()
    {
        // Arrange: nothing covers a 1 BTC/kB package
        BroadcastCommitment();
        _estimate = 100_000_000;

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_store.Children);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_ChannelWithoutAnchors_When_CommitmentBroadcast_Then_Nothing()
    {
        // Arrange
        Dispose();
        Init(hasAnchors: false);
        BroadcastCommitment();

        // Act
        await Service.OnCommitmentBroadcastAsync(_channel.ChannelId, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_store.Children);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_AnchorCommitment_When_CommitmentBroadcast_Then_ChildAtOnce()
    {
        // Arrange
        BroadcastCommitment();

        // Act
        await Service.OnCommitmentBroadcastAsync(_channel.ChannelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_store.Children);
        Assert.Single(_published);
    }

    [Fact]
    public async Task Given_AnchorCommitment_When_RoundScheduled_Then_ChildMadeInTheBackground()
    {
        // Arrange
        BroadcastCommitment();
        var service = Service;

        // Act: returns at once (the fail-the-channel path must not wait for it)
        service.ScheduleCommitmentRound(_channel.ChannelId);
        await service.WhenIdleAsync();

        // Assert
        Assert.Single(_store.Children);
        Assert.Single(_published);
    }

    [Fact]
    public async Task Given_CommitmentConfirmed16BlocksAgo_When_FeesAreLow_Then_BothAnchorsSweptOnce()
    {
        // Arrange: a commitment that paid its way (no child), confirmed at 500
        _estimate = 2_000;
        var commitment = BroadcastCommitment();
        commitment.MarkConfirmed(500, OnchainTestStore.BlockHash(1));
        _estimate = 253;

        // Act: at tip 514 the next block (515) is only 15 deep; at 515 the next is 16
        await Service.RunOnceAsync(514, TestContext.Current.CancellationToken);
        var before = _sweeps.Count;
        await Service.RunOnceAsync(515, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(516, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, before);
        var sweep = Transaction.Load(Assert.Single(_sweeps).RawTxBytes, Network.Main);
        var commitmentTx = Load(commitment);
        Assert.Equal(2, sweep.Inputs.Count);
        Assert.All(sweep.Inputs, i => Assert.Equal(commitmentTx.GetHash(), i.PrevOut.Hash));
        for (var i = 0; i < sweep.Inputs.Count; i++)
            Assert.True(sweep.Inputs.AsIndexedInputs().ElementAt(i)
                             .VerifyScript(commitmentTx.Outputs[sweep.Inputs[i].PrevOut.N], out var error),
                        error.ToString());
        Assert.Equal(_walletScript, sweep.Outputs.Single().ScriptPubKey.ToBytes());
        Assert.Empty(_store.Children);
    }

    [Fact]
    public async Task Given_OurAnchorSpentOnChain_When_SweepIsDue_Then_SpentAnchorNeverSwept()
    {
        // Arrange: a child the monitor no longer tracked (replaced, or confirmed after its round) spent our anchor;
        // the peer's anchor alone never pays for its sweep
        _estimate = 2_000;
        var commitment = BroadcastCommitment();
        commitment.MarkConfirmed(500, OnchainTestStore.BlockHash(1));
        _estimate = 253;
        var commitmentTx = Load(commitment);
        _chain.Spent.Add(new OutPoint(commitmentTx.GetHash(), FindOurAnchor(commitmentTx)));

        // Act
        await Service.RunOnceAsync(515, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(516, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_sweeps);
    }

    [Fact]
    public async Task Given_ChainUnreachable_When_SweepIsDue_Then_SweptAtTheNextBlock()
    {
        // Arrange
        _estimate = 2_000;
        var commitment = BroadcastCommitment();
        commitment.MarkConfirmed(500, OnchainTestStore.BlockHash(1));
        _estimate = 253;
        _chain.Throws = true;

        // Act
        await Service.RunOnceAsync(515, TestContext.Current.CancellationToken);
        var before = _sweeps.Count;
        _chain.Throws = false;
        await Service.RunOnceAsync(516, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, before);
        Assert.Equal(2, Transaction.Load(Assert.Single(_sweeps).RawTxBytes, Network.Main).Inputs.Count);
    }

    [Fact]
    public async Task Given_CommitmentConfirmed16BlocksAgo_When_FeesAreHigh_Then_AnchorsNotSwept()
    {
        // Arrange
        _estimate = 2_000;
        var commitment = BroadcastCommitment();
        commitment.MarkConfirmed(500, OnchainTestStore.BlockHash(1));
        _estimate = 2_500;

        // Act
        await Service.RunOnceAsync(515, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_sweeps);
    }

    [Fact]
    public async Task Given_NewBlocks_When_Started_Then_RoundsRunInTheBackground()
    {
        // Arrange
        BroadcastCommitment();
        var service = Service;
        service.Start();

        // Act
        _monitor.Raise(m => m.OnNewBlockDetected += null,
                       new Domain.Bitcoin.Events.NewBlockEventArgs(500, OnchainTestStore.BlockHash(2)));
        await service.WhenIdleAsync();
        service.Stop();

        // Assert
        Assert.Single(_store.Children);
    }

    public void Dispose()
    {
        foreach (var restarted in _restarted)
            restarted.Dispose();
        _restarted.Clear();
        _provider.Dispose();
        _pair.Dispose();
    }

    /// <summary>
    /// Alice offers an HTLC (expiry 600; 20,000 sat unless given, below her 546 sat dust limit it is trimmed), the
    /// dance settles, and she fails the channel: our commitment row.
    /// </summary>
    private BroadcastTransactionModel BroadcastCommitment(ulong htlcMsat = 20_000_000)
    {
        _pair.Add(_pair.Alice, htlcMsat, RealSigningCommitmentPair.Preimage(1), HtlcExpiry);
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _channel.UpdateState(ChannelState.Failed);

        var builder = new LocalCommitmentBroadcastBuilder(
            _provider.GetRequiredService<ICommitmentTransactionModelFactory>(),
            _provider.GetRequiredService<ICommitmentTransactionBuilder>(), _pair.Alice.Signer);
        var signed = builder.Build(_channel);
        var row = new BroadcastTransactionModel(signed.Transaction, BroadcastPurpose.LocalCommitment,
                                                _channel.ChannelId, 500, commitmentNumber: signed.CommitmentNumber);
        _store.Add(row);
        return row;
    }

    private uint FindOurAnchor(Transaction commitment) =>
        new AnchorChildTransactionBuilder().FindAnchorOutput(commitment.ToBytes(),
                                                             _channel.LocalKeySet.FundingCompactPubKey)
     ?? throw new InvalidOperationException("No anchor");

    private ulong PackageFeerate(Transaction commitment, Transaction child)
    {
        var commitmentFee = RealSigningCommitmentPair.FundingSatoshis - AnchorTx.OutputsSat(commitment);
        var childFee = AnchorTx.ChildFee(child, _wallet);
        return (commitmentFee + childFee) * 1000
             / (ulong)(AnchorTx.Weight(commitment) + AnchorTx.Weight(child));
    }

    private static Transaction Load(BroadcastTransactionModel row) => Transaction.Load(row.RawTransaction, Network.Main);
}