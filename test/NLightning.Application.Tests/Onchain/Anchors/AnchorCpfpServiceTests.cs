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
using Domain.Onchain.Events;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Bitcoin.Wallet.Models;

/// <summary>
/// BOLT 5 plan O7-T2 (B5-FAIL-06): <see cref="AnchorCpfpService"/> over a <b>real</b> anchor commitment of a
/// <see cref="RealSigningCommitmentPair"/> (Alice fails the channel with an HTLC in flight): the child spends our anchor
/// (script-executed) and wallet inputs, the package pays the estimate, an RBF replacement raises the fee over BIP 125's
/// minimum and replaces the old row, the reservation is released once the commitment confirmed or lost, and the anchors
/// are swept 16 blocks after the confirmation only when that pays for itself.
/// </summary>
public sealed partial class AnchorCpfpServiceTests : IDisposable
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

    private void Init(bool hasAnchors, bool simpleTaproot = false)
    {
        _pair = new RealSigningCommitmentPair(hasAnchors, simpleTaproot);
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
        // NL-611: an anchor sweep is a stored AnchorSweep row published like a child; the tests keep it apart
        _monitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
                .Callback<BroadcastTransactionModel>(row =>
                 {
                     if (row.Purpose == BroadcastPurpose.AnchorSweep)
                         _sweeps.Add(row.ToSignedTransaction());
                     else
                         _published.Add(row);
                 })
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
        _destination.Setup(d => d.GetDestinationScriptAsync(It.IsAny<Domain.Channels.ValueObjects.ChannelId>(), It.IsAny<CancellationToken>())).ReturnsAsync(_walletScript);

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
        services.AddSingleton<Application.Onchain.Fees.OperatorFeeBumps>();
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
        Assert.Equal((long)AnchorTx.ChildFee(child, _wallet), row.Fee?.Satoshi); // NL-604
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
    public async Task Given_CommitmentPayingTheEstimateButNotTheMempoolMinimum_When_Round_Then_ChildMadeAndPackaged()
    {
        // Arrange: the commitment pays 2,500 sat/kw, above the 2,000 sat/kw estimate but below bitcoind's 5,000 sat/kw
        // mempool minimum: refused alone, so its child is an orphan and only a package gets both in
        var commitment = BroadcastCommitment();
        _estimate = 2_000;
        _chain.MempoolMinFeePerKw = 5_000;
        RefuseChildrenAlone();
        _chain.PackageAnswer = _chain.AcceptPackage;

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert: the child targets the mempool minimum, and the pair went out as a package
        var child = Assert.Single(_store.Children);
        Assert.True(PackageFeerate(Load(commitment), Load(child)) >= 5_000);
        var (parent, packaged) = Assert.Single(_chain.Packages);
        Assert.Equal(Load(commitment).GetHash(), parent.GetHash());
        Assert.Equal(Load(child).GetHash(), packaged.GetHash());
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
        Assert.Equal((long)newFee, replacement.Fee?.Satoshi); // NL-604
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
    public async Task Given_PendingChildPayingTheEstimateWithoutDeadline_When_BumpIsDue_Then_Kept()
    {
        // Arrange: only a trimmed HTLC, so nothing has a deadline
        BroadcastCommitment(htlcMsat: 500_000);
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        Assert.Single(_store.Children);

        // Act: same estimate, the package still pays it, well past the no-deadline target
        await Service.RunOnceAsync(500 + new AnchorCpfpOptions().NoDeadlineConfTarget + 10,
                                   TestContext.Current.CancellationToken);

        // Assert
        var child = Assert.Single(_store.Children);
        Assert.Equal(BroadcastState.Pending, child.State);
        Assert.Single(_published);
    }

    [Fact]
    public async Task Given_PendingChildPayingTheEstimateWithDeadline_When_BumpIsDue_Then_ReplacedAtTheBip125Minimum()
    {
        // Arrange: the HTLC gives the commitment a deadline; blocks keep leaving the package out at the same estimate
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var first = Assert.Single(_store.Children);

        // Act
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert: replaced with at least the BIP 125 minimum over the old fee (integrated wave O7: as SweepScheduler)
        Assert.Equal(BroadcastState.Replaced, first.State);
        var replacement = _store.Children.Single(c => c.TransactionId != first.TransactionId);
        var oldFee = AnchorTx.ChildFee(Load(first), _wallet);
        var newChild = Load(replacement);
        var newFee = AnchorTx.ChildFee(newChild, _wallet);
        Assert.True(newFee >= oldFee * 5 / 4, $"{newFee} < 1.25 x {oldFee}");
        AnchorTx.AssertScriptsValid(newChild, Load(commitment), _wallet);
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
    public async Task Given_ReorgUnconfirmsAChildWhoseReservationEnded_When_Disconnect_Then_ItsInputsAreReReserved()
    {
        // Arrange: a pending child whose inputs the chain monitor removed (its reservation ended when the child first
        // confirmed); the reorg puts them back into the wallet, still spent by the pending child row
        BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        var spentByChild = Load(child).Inputs.Skip(1)
                                        .Select(i => new OutPoint(new uint256(i.PrevOut.Hash.ToBytes()), i.PrevOut.N))
                                        .ToList();
        await _wallet.ReleaseAsync(_channel.ChannelId, TestContext.Current.CancellationToken);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
        Service.Start();
        try
        {
            // Act: the block that held the child is disconnected
            _monitor.Raise(m => m.OnBlockDisconnected += null,
                           new BlockDisconnectedEventArgs(501, OnchainTestStore.BlockHash(1), 500));
            await Service.WhenIdleAsync();

            // Assert: the child's wallet inputs are reserved for the channel again
            var reserved = _wallet.Reserved(_channel.ChannelId).Select(i => new OutPoint(new uint256(i.TxId), i.OutputIndex)).ToList();
            Assert.Equal(spentByChild, reserved);
        }
        finally
        {
            Service.Stop();
        }
    }

    [Fact]
    public async Task Given_AChildStillHoldingItsReservation_When_Disconnect_Then_NothingExtraIsReserved()
    {
        // Arrange
        BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var held = _wallet.Reserved(_channel.ChannelId).ToList();
        Service.Start();
        try
        {
            // Act
            _monitor.Raise(m => m.OnBlockDisconnected += null,
                           new BlockDisconnectedEventArgs(501, OnchainTestStore.BlockHash(1), 500));
            await Service.WhenIdleAsync();

            // Assert: the held reservation is left alone
            Assert.Equal(held, _wallet.Reserved(_channel.ChannelId));
        }
        finally
        {
            Service.Stop();
        }
    }

    [Fact]
    public async Task Given_WaitPassedWithBitcoindUnreachable_When_TheChildSettles_Then_ItsInputsAreSpentBackToTheWallet()
    {
        // Arrange: bitcoind is unreachable, so only the wait ends it (NL-386); the child may still be alive in other
        // mempools, so releasing its inputs alone would let a funding conflict with it
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        var childTx = Load(child);
        var childFee = childTx.Inputs.Skip(1).Aggregate(0UL, (sum, i) => sum + (ulong)_wallet.GetSpentOutput(i.PrevOut)!
                                                                                      .Value.Satoshi)
                    - AnchorTx.OutputsSat(childTx);
        commitment.MarkConfirmed(501, OnchainTestStore.BlockHash(1));
        _chain.Throws = true;
        var wait = new AnchorCpfpOptions().ConfirmedCommitmentChildWaitBlocks;

        // Act
        await Service.RunOnceAsync(501 + wait, TestContext.Current.CancellationToken);

        // Assert: one reclaim, spending the child's wallet inputs back to the wallet at a fee that replaces the child
        var reclaim = Assert.Single(_store.Rows, r => r.Purpose == BroadcastPurpose.WalletSend);
        Assert.Equal(BroadcastState.Pending, reclaim.State);
        Assert.Equal(reclaim, Assert.Single(_published.Skip(1)));
        var rescue = Load(reclaim);
        var rescueInputs = rescue.Inputs.Select(i => i.PrevOut).ToList();
        Assert.All(childTx.Inputs.Skip(1).Select(i => i.PrevOut), spent => Assert.Contains(spent, rescueInputs));
        Assert.Single(rescue.Outputs);
        Assert.Equal(_walletScript, rescue.Outputs[0].ScriptPubKey.ToBytes());
        Assert.Equal(0xFFFFFFFDu, (uint)rescue.Inputs[0].Sequence);
        Assert.All(rescue.Inputs.AsIndexedInputs(),
                   i => Assert.True(i.VerifyScript(_wallet.GetSpentOutput(i.PrevOut)!, out var error), $"{error}"));
        var rescueFee = rescueInputs.Aggregate(0UL, (sum, o) => sum + (ulong)_wallet.GetSpentOutput(o)!.Value.Satoshi)
                       - AnchorTx.OutputsSat(rescue);
        Assert.True(rescueFee > childFee + (ulong)rescue.GetVirtualSize(), $"{rescueFee} does not replace {childFee}");
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_TheAnchorSpentBySomeoneElse_When_TheChildSettles_Then_ReleasedWithoutAReclaim()
    {
        // Arrange: the child's parent output was spent in the active chain, so the child is dead in every mempool
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        commitment.MarkConfirmed(501, OnchainTestStore.BlockHash(1));
        var anchorVout = FindOurAnchor(Load(commitment));
        _chain.Spent.Add(new OutPoint(Load(commitment).GetHash(), anchorVout));
        _chain.Tip = 507;

        // Act: the first round records the spend at bitcoind's tip; the second knows the monitor caught up
        await Service.RunOnceAsync(507, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(507, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
        Assert.DoesNotContain(_store.Rows, r => r.Purpose == BroadcastPurpose.WalletSend);
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

        // NL-611: the sweep was stored as an AnchorSweep row of the channel, with its fee, before it was published
        var row = Assert.Single(_store.Rows, r => r.Purpose == BroadcastPurpose.AnchorSweep);
        Assert.Equal(new TxId(sweep.GetHash().ToBytes()), row.TransactionId);
        Assert.Equal(_channel.ChannelId, row.ChannelId);
        Assert.Equal(BroadcastState.Pending, row.State);
        Assert.Equal(660 - sweep.Outputs.Single().Value.Satoshi, (long)row.Fee!.Satoshi);
    }

    [Fact]
    public async Task Given_ARefusedAnchorSweep_When_Published_Then_ItsRowIsGivenUpAndNotRetried()
    {
        // Arrange (NL-611): bitcoind refuses the sweep (someone took an anchor first)
        _estimate = 2_000;
        var commitment = BroadcastCommitment();
        commitment.MarkConfirmed(500, OnchainTestStore.BlockHash(1));
        _estimate = 253;
        _monitor.Setup(m => m.PublishAsync(It.Is<BroadcastTransactionModel>(r => r.Purpose == BroadcastPurpose.AnchorSweep)))
                .Callback<BroadcastTransactionModel>(r => _sweeps.Add(r.ToSignedTransaction()))
                .ReturnsAsync(false);

        // Act
        await Service.RunOnceAsync(515, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(516, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_sweeps);
        Assert.Equal(BroadcastState.Abandoned,
                     Assert.Single(_store.Rows, r => r.Purpose == BroadcastPurpose.AnchorSweep).State);
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

    [Fact]
    public async Task Given_ChildRefusedAsAnOrphan_When_Round_Then_CommitmentAndChildSubmittedAsAPackage()
    {
        // Arrange: the commitment is below bitcoind's mempool minimum, so the child alone is an orphan (NL-380)
        var commitment = BroadcastCommitment();
        RefuseChildrenAlone();
        _chain.PackageAnswer = _chain.AcceptPackage;

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert: parent first, the child row stays pending (the monitor keeps it for rebroadcast)
        var child = Assert.Single(_store.Children);
        var (parent, packaged) = Assert.Single(_chain.Packages);
        Assert.Equal(Load(commitment).GetHash(), parent.GetHash());
        Assert.Equal(Load(child).GetHash(), packaged.GetHash());
        Assert.Equal(commitment.RawTransaction, parent.ToBytes());
        Assert.Equal(child.RawTransaction, packaged.ToBytes());
        Assert.Equal(BroadcastState.Pending, child.State);
        Assert.Equal(child, Assert.Single(_published));
    }

    [Fact]
    public async Task Given_ChildAccepted_When_Round_Then_NoPackage()
    {
        // Arrange: the monitor's publish succeeds, so the commitment is in the mempool with it
        BroadcastCommitment();

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(_store.Children);
        Assert.Empty(_chain.Packages);
    }

    [Fact]
    public async Task Given_PendingChildMissingFromTheMempool_When_NextRound_Then_PairSentAgainAsAPackage()
    {
        // Arrange: accepted at 500, then evicted with its commitment (bitcoind no longer has the child); only a trimmed
        // HTLC, so no deadline bump replaces the child meanwhile
        var commitment = BroadcastCommitment(htlcMsat: 500_000);
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        _chain.PackageAnswer = _chain.AcceptPackage;

        // Act: no bump is due at 501; the round checks the child and sends the pair
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);
        var afterFirst = _chain.Packages.Count;
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert: once; at 502 bitcoind has it (the package made it in) and it still pays the estimate
        Assert.Equal(1, afterFirst);
        var (parent, packaged) = Assert.Single(_chain.Packages);
        Assert.Equal(Load(commitment).GetHash(), parent.GetHash());
        Assert.Equal(Load(child).GetHash(), packaged.GetHash());
        Assert.Equal(BroadcastState.Pending, child.State);
    }

    [Fact]
    public async Task Given_PendingChildInTheMempool_When_NextRound_Then_NoPackage()
    {
        // Arrange
        BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        _chain.Mempool.Add(Load(Assert.Single(_store.Children)).GetHash());

        // Act
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_chain.Packages);
    }

    [Fact]
    public async Task Given_NodeWithoutPackageRelay_When_ChildRefused_Then_RowsStayPendingForTheMonitorsRebroadcast()
    {
        // Arrange: submitpackage is missing (older bitcoind): the fallback is the one-by-one rebroadcast
        var commitment = BroadcastCommitment();
        RefuseChildrenAlone();

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert: asked, nothing changed; the child was handed to the monitor, which resends every pending row
        Assert.NotEmpty(_chain.Packages);
        var child = Assert.Single(_store.Children);
        Assert.Equal(BroadcastState.Pending, child.State);
        Assert.Equal(BroadcastState.Pending, commitment.State);
        Assert.Contains(child, _published);
        Assert.Equal(0, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_PairStoredBeforeARestart_When_FirstRoundAfterIt_Then_RebroadcastAsAPackage()
    {
        // Arrange: before the restart bitcoind refused the child and had no package relay; after it, it has
        var commitment = BroadcastCommitment();
        RefuseChildrenAlone();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        _chain.Packages.Clear();
        var restarted = BuildProvider();
        _restarted.Add(restarted);
        var service = restarted.GetRequiredService<AnchorCpfpService>();
        _chain.PackageAnswer = _chain.AcceptPackage;

        // Act: the rows come back from the store; no bump is due at 501
        await service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert: the persisted pair, byte for byte, as one package; nothing new signed or reserved
        var (parent, packaged) = Assert.Single(_chain.Packages);
        Assert.Equal(commitment.RawTransaction, parent.ToBytes());
        Assert.Equal(child.RawTransaction, packaged.ToBytes());
        Assert.Single(_store.Children);
        Assert.Contains(Load(child).GetHash(), _chain.Mempool);
    }

    [Fact]
    public async Task Given_PackageRefusedForFee_When_Round_Then_TheChildIsReplacedInTheSameRound()
    {
        // Arrange: the estimate is below bitcoind's mempool minimum; the first package is refused for its fee
        var commitment = BroadcastCommitment();
        RefuseChildrenAlone();
        _chain.PackageAnswer = FakeAnchorChain.RefuseForFee;

        // Act: the refusal is acted on in the same round — no next block is needed (NL-391)
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert: replaced at the BIP 125 minimum at least; both pairs went out, the replacement's still refused
        Assert.Equal(2, _chain.Packages.Count);
        var first = _store.Children[0];
        var replacement = Assert.Single(_store.Children, c => c.State == BroadcastState.Pending);
        Assert.Equal(BroadcastState.Replaced, first.State);
        Assert.Equal(first.TransactionId, replacement.ReplacesTransactionId);
        var oldFee = AnchorTx.ChildFee(Load(first), _wallet);
        var newChild = Load(replacement);
        var newFee = AnchorTx.ChildFee(newChild, _wallet);
        Assert.True(newFee >= oldFee * 5 / 4, $"{newFee} < 1.25 x {oldFee}");
        Assert.True(newFee >= oldFee + (ulong)newChild.GetVirtualSize(), $"{newFee} below the relay increment");
        AnchorTx.AssertScriptsValid(newChild, Load(commitment), _wallet);
        Assert.Equal(Load(first).GetHash(), _chain.Packages[0].Child.GetHash());
        Assert.Equal(newChild.GetHash(), _chain.Packages[1].Child.GetHash());

        // One block later the higher package is taken
        _chain.PackageAnswer = _chain.AcceptPackage;
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);
        Assert.Equal(3, _chain.Packages.Count);
        Assert.Contains(_chain.Packages[2].Child.GetHash(), _chain.Mempool);
        Assert.Single(_store.Children, c => c.State == BroadcastState.Pending);
    }

    [Fact]
    public async Task Given_PairStoredBeforeARestart_When_ItsFirstPackageIsRefusedForFee_Then_ReplacedInTheSameRound()
    {
        // Arrange: before the restart bitcoind refused the pair for its fee (the mark was in memory); after it, the
        // first round sees the refusal itself and acts in the same round — no block lost (NL-391)
        BroadcastCommitment();
        RefuseChildrenAlone();
        _chain.PackageAnswer = FakeAnchorChain.RefuseForFee;
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        _chain.Packages.Clear();
        var restarted = BuildProvider();
        _restarted.Add(restarted);
        var service = restarted.GetRequiredService<AnchorCpfpService>();
        var stored = Assert.Single(_store.Children, c => c.State == BroadcastState.Pending);
        var refusals = 0;
        _chain.PackageAnswer = (parent, child) => refusals++ == 0
            ? FakeAnchorChain.RefuseForFee(parent, child)
            : _chain.AcceptPackage(parent, child);

        // Act: the first round after the restart: the stored pair goes out, is refused for its fee, replaced at once
        await service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert: the stored child was replaced in the same round; the replacement entered with the commitment
        Assert.Equal(BroadcastState.Replaced, stored.State);
        var second = Assert.Single(_store.Children, c => c.State == BroadcastState.Pending);
        Assert.Equal(stored.TransactionId, second.ReplacesTransactionId);
        Assert.Equal(2, _chain.Packages.Count);
        Assert.Equal(Load(stored).GetHash(), _chain.Packages[0].Child.GetHash());
        Assert.Equal(Load(second).GetHash(), _chain.Packages[1].Child.GetHash());
        Assert.Contains(Load(second).GetHash(), _chain.Mempool);
    }

    [Fact]
    public async Task Given_PackageWithoutDeadlineRefusedForFee_When_Round_Then_ReplacedAlthoughItPaysTheEstimate()
    {
        // Arrange: only a trimmed HTLC (no deadline); a package that pays the estimate is normally kept
        BroadcastCommitment(htlcMsat: 500_000);
        RefuseChildrenAlone();
        _chain.PackageAnswer = FakeAnchorChain.RefuseForFee;

        // Act: the fee refusal acts in the same round although no deadline forces a bump (NL-391)
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, _store.Children.Count);
        Assert.Equal(BroadcastState.Replaced, _store.Children[0].State);
        Assert.Equal(BroadcastState.Pending, _store.Children[1].State);
    }

    [Fact]
    public async Task Given_PackageRefusedForAnotherReason_When_NextBlock_Then_ChildKeptUntilTheBumpIsDue()
    {
        // Arrange: the funding output is spent (not a fee problem): nothing to gain from a replacement
        BroadcastCommitment();
        RefuseChildrenAlone();
        _chain.PackageAnswer = (parent, child) => new PackageSubmitResult(
            PackageSubmitStatus.Rejected, "transaction failed",
            [
                new PackageTransactionResult(parent.GetHash(), false, "bad-txns-inputs-missingorspent", null),
                new PackageTransactionResult(child.GetHash(), false, "unevaluated", null)
            ]);
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var first = Assert.Single(_store.Children);

        // Act
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BroadcastState.Pending, first.State);
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

    [Fact]
    public async Task Given_SimpleTaprootCommitmentBelowTheEstimate_When_Round_Then_ChildSpendsOurAnchorByKeyPath()
    {
        // Arrange (NL-966): a failed taproot channel whose MuSig2-signed commitment is broadcast; our anchor on it is
        // P2TR, keyed to our local_delayedpubkey of that commitment with the OP_16 OP_CSV leaf
        Dispose();
        Init(hasAnchors: true, simpleTaproot: true);
        var commitment = BroadcastCommitment();

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert: one child spending our taproot anchor first, by key path (one 64-byte item), every input valid over
        // all the outputs it spends; the package pays the estimate
        var row = Assert.Single(_store.Children);
        Assert.Equal(row, Assert.Single(_published));
        var commitmentTx = Load(commitment);
        var child = Load(row);
        Assert.Equal(new OutPoint(commitmentTx.GetHash(), FindOurTaprootAnchor(commitmentTx)), child.Inputs[0].PrevOut);
        Assert.Equal(64, Assert.Single(child.Inputs[0].WitScript.Pushes).Length);
        AnchorTx.AssertAllScriptsValid(child, commitmentTx, _wallet);
        Assert.InRange(PackageFeerate(commitmentTx, child), _estimate, _estimate + 50);

        // Act: the estimate rises and the bump is due (RbfIntervalBlocks 2)
        _estimate = 20_000;
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert: the replacement spends the same anchor, signed again over its new spent outputs, valid
        var replacement = _store.Children.Single(c => c.TransactionId != row.TransactionId);
        Assert.Equal(row.TransactionId, replacement.ReplacesTransactionId);
        Assert.Equal(BroadcastState.Replaced, row.State);
        var newChild = Load(replacement);
        Assert.Equal(child.Inputs[0].PrevOut, newChild.Inputs[0].PrevOut);
        AnchorTx.AssertAllScriptsValid(newChild, commitmentTx, _wallet);
        Assert.True(AnchorTx.ChildFee(newChild, _wallet) > AnchorTx.ChildFee(child, _wallet));
    }

    [Fact]
    public async Task Given_SimpleTaprootCommitmentConfirmed16BlocksAgo_When_FeesAreLow_Then_BothAnchorsSweptByTheLeaf()
    {
        // Arrange (NL-966): a taproot commitment that paid its way, confirmed at 500
        Dispose();
        Init(hasAnchors: true, simpleTaproot: true);
        _estimate = 2_000;
        var commitment = BroadcastCommitment();
        commitment.MarkConfirmed(500, OnchainTestStore.BlockHash(1));
        _estimate = 253;

        // Act
        await Service.RunOnceAsync(514, TestContext.Current.CancellationToken);
        var before = _sweeps.Count;
        await Service.RunOnceAsync(515, TestContext.Current.CancellationToken);

        // Assert: both P2TR anchors (ours and the peer's) swept by <OP_16 OP_CSV> <control block>, nSequence 16, valid
        Assert.Equal(0, before);
        var sweep = Transaction.Load(Assert.Single(_sweeps).RawTxBytes, Network.Main);
        var commitmentTx = Load(commitment);
        Assert.Equal(2, sweep.Inputs.Count);
        Assert.Contains(sweep.Inputs, i => i.PrevOut.N == FindOurTaprootAnchor(commitmentTx));
        Assert.All(sweep.Inputs, i =>
        {
            Assert.Equal(commitmentTx.GetHash(), i.PrevOut.Hash);
            Assert.Equal(16u, i.Sequence.Value);
            Assert.Equal(2, i.WitScript.PushCount);
            Assert.Equal(330, commitmentTx.Outputs[i.PrevOut.N].Value.Satoshi);
        });
        AnchorTx.AssertAllScriptsValid(sweep, commitmentTx, _wallet);
        Assert.Equal(_walletScript, sweep.Outputs.Single().ScriptPubKey.ToBytes());
    }

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

    /// <summary>bitcoind refuses every child sent alone (its commitment is not in the mempool).</summary>
    private void RefuseChildrenAlone() =>
        _monitor.Setup(m => m.PublishAsync(It.Is<BroadcastTransactionModel>(b => b.Purpose
                                                                               == BroadcastPurpose.AnchorCpfp)))
                .Callback<BroadcastTransactionModel>(_published.Add)
                .ReturnsAsync(false);

    /// <summary>Our taproot anchor on our commitment: keyed to our local_delayedpubkey of its number.</summary>
    private uint FindOurTaprootAnchor(Transaction commitment)
    {
        var keys = _provider.GetRequiredService<ICommitmentKeyDerivationService>().DeriveLocalCommitmentKeys(
            _channel.LocalKeySet.KeyIndex, _pair.Alice.Signer.GetChannelBasepoints(_channel.LocalKeySet.KeyIndex),
            _pair.Bob.Basepoints, _pair.Alice.State.LocalCommit.Number);
        return new AnchorChildTransactionBuilder().FindTaprootAnchorOutput(commitment.ToBytes(),
                                                                           keys.LocalDelayedPubKey)
            ?? throw new InvalidOperationException("No taproot anchor");
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