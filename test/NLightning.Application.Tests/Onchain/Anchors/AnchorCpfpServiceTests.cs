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
    private uint _estimate = 10_000;
    private bool _walletSigns = true;

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

        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => LightningMoney.Satoshis(_estimate));
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => LightningMoney.Satoshis(_estimate));
        var destination = new Mock<ISweepDestinationProvider>();
        destination.Setup(d => d.GetDestinationScriptAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_walletScript);

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(_store);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => _store.Saves++).Returns(Task.CompletedTask);

        var signer = WalletSigningProxy.Create(_pair.Alice.Signer,
                                               tx => _walletSigns
                                                         ? _wallet.SignWalletInputs(tx)
                                                         : throw new NotImplementedException());

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton(signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddSingleton(_monitor.Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton(feeService.Object);
        services.AddSingleton(destination.Object);
        services.AddSingleton<IAnchorFeeInputSource>(_wallet);
        services.AddSingleton(new SweepFeePolicy());
        services.AddScoped(_ => unitOfWork.Object);
        services.AddAnchorCpfpServices();
        _provider = services.BuildServiceProvider();
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_CommitmentConfirmed_When_Round_Then_PendingChildAbandonedAndReservationReleasedOnce(
        bool childConfirmed)
    {
        // Arrange
        var commitment = BroadcastCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        commitment.MarkConfirmed(501, OnchainTestStore.BlockHash(1));
        if (childConfirmed)
            child.MarkConfirmed(501, OnchainTestStore.BlockHash(1));

        // Act
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(childConfirmed ? BroadcastState.Confirmed : BroadcastState.Abandoned, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
        Assert.Single(_store.Children);
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
        _provider.Dispose();
        _pair.Dispose();
    }

    /// <summary>Alice offers an HTLC (expiry 600), the dance settles, and she fails the channel: our commitment row.
    /// </summary>
    private BroadcastTransactionModel BroadcastCommitment()
    {
        _pair.Add(_pair.Alice, 20_000_000, RealSigningCommitmentPair.Preimage(1), HtlcExpiry);
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