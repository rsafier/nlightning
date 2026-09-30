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
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-381 (BOLT 5 plan O7-T2): <see cref="AnchorCpfpService"/> fee-bumps the <b>peer's</b> unconfirmed commitment
/// through the anchor keyed to our funding pubkey on it. The peer's commitment is Bob's real signed commitment of a
/// <see cref="RealSigningCommitmentPair"/> with anchors and an HTLC in flight; we are Alice. The child spends our anchor
/// on it (script-executed) and wallet inputs, is replaced (RBF) like ours, keeps its inputs until it can no longer
/// confirm, is abandoned when the peer's commitment leaves the mempool, and the anchors of the confirmed commitment are
/// swept 16 blocks later.
/// </summary>
public sealed class AnchorPeerCpfpTests : IDisposable
{
    private const uint HtlcExpiry = 600;

    private readonly RealSigningCommitmentPair _pair = new(hasAnchors: true);
    private readonly ChannelModel _channel;
    private readonly InMemoryBroadcasts _store = new();
    private readonly FakeAnchorWallet _wallet = new();
    private readonly FakeAnchorChain _chain = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IOnchainResolutionDbRepository> _resolutions = new();
    private readonly List<BroadcastTransactionModel> _published = [];
    private readonly List<SignedTransaction> _sweeps = [];
    private readonly List<ServiceProvider> _providers = [];
    private readonly byte[] _walletScript = new Key(Enumerable.Repeat((byte)0x34, 32).ToArray())
                                            .PubKey.WitHash.ScriptPubKey.ToBytes();
    private readonly ILightningSigner _signer;
    private readonly ServiceProvider _provider;
    private ChannelCloseModel? _close;
    private uint _estimate = 10_000;

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    public AnchorPeerCpfpTests()
    {
        _channel = _pair.Alice.Channel;
        _wallet.AddUtxo(0x61, 50_000);
        _wallet.AddUtxo(0x62, 30_000);
        _wallet.AddUtxo(0x63, 200_000);

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
        _resolutions.Setup(r => r.GetCloseAsync(It.IsAny<ChannelId>())).ReturnsAsync(() => _close);

        _signer = WalletSigningProxy.Create(_pair.Alice.Signer, _wallet.SignWalletInputs);
        _provider = BuildProvider();
    }

    private AnchorCpfpService Service => _provider.GetRequiredService<AnchorCpfpService>();

    [Fact]
    public async Task Given_PeerCommitmentHandedOverFromTheMempool_When_Round_Then_ChildSpendsOurAnchorOnItScriptValid()
    {
        // Arrange: Bob force-closed; his commitment (with our HTLC, expiry 600) is in bitcoind's mempool and the
        // channel is still Open on our side
        var peer = PeerCommitmentInMempool();

        // Act: the mempool reactor's hand-over schedules the channel's round
        Service.OnPeerCommitmentInMempool(_channel.ChannelId, ToSigned(peer), false);
        await Service.WhenIdleAsync();

        // Assert: one child, published, spending our anchor on Bob's commitment first
        var row = Assert.Single(_store.Children);
        Assert.Equal(BroadcastState.Pending, row.State);
        Assert.Equal(row, Assert.Single(_published));
        var child = Load(row);
        Assert.Equal(new OutPoint(peer.GetHash(), OurAnchorOn(peer)), child.Inputs[0].PrevOut);
        Assert.Equal(_walletScript, Assert.Single(child.Outputs).ScriptPubKey.ToBytes());
        AnchorTx.AssertScriptsValid(child, peer, _wallet);

        // The package (Bob's commitment + our child) pays the estimate for the HTLC's deadline
        Assert.InRange(PackageFeerate(peer, child), _estimate, _estimate + 50);
        Assert.Equal(child.Inputs.Count - 1, _wallet.Reserved(_channel.ChannelId).Count);
        Assert.Equal(0, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_HandedOverPeerCommitmentBelowTheMempoolMinimum_When_Round_Then_ItIsPackagedWithOurChild()
    {
        // Arrange: the mempool reactor handed Bob's commitment over, but our bitcoind does not have it (its mempool
        // minimum is above what it pays): the child alone is refused as an orphan, only the handed-over bytes exist
        var peer = PeerCommitmentInMempool();
        _chain.Transactions.Remove(peer.GetHash());
        _monitor.Setup(m => m.PublishAsync(It.Is<BroadcastTransactionModel>(b => b.Purpose
                                                                                == BroadcastPurpose.AnchorCpfp)))
                .Callback<BroadcastTransactionModel>(_published.Add)
                .ReturnsAsync(false);
        _chain.PackageAnswer = _chain.AcceptPackage;

        // Act
        Service.OnPeerCommitmentInMempool(_channel.ChannelId, ToSigned(peer), false);
        await Service.WhenIdleAsync();

        // Assert: the handed-over bytes and our child went in as one package
        var row = Assert.Single(_store.Children);
        var (parent, packaged) = Assert.Single(_chain.Packages);
        Assert.Equal(peer.GetHash(), parent.GetHash());
        Assert.Equal(peer.ToBytes(), parent.ToBytes());
        Assert.Equal(Load(row).GetHash(), packaged.GetHash());
        AnchorTx.AssertScriptsValid(Load(row), peer, _wallet);
        Assert.Equal(row, Assert.Single(_published));
    }

    [Fact]
    public async Task Given_PendingChildOfTheHandedOverCommitment_When_NoNewChildIsDue_Then_ThePairIsSentAsAPackage()
    {
        // Arrange: the first child went out when bitcoind still had the commitment; then it dropped out of the mempool
        var peer = PeerCommitmentInMempool();
        Service.OnPeerCommitmentInMempool(_channel.ChannelId, ToSigned(peer), false);
        await Service.WhenIdleAsync();
        var child = Assert.Single(_store.Children);
        _chain.Packages.Clear();
        _chain.Transactions.Remove(peer.GetHash());
        _chain.PackageAnswer = _chain.AcceptPackage;

        // Act: no replacement is due at 501 (the RBF interval has not passed); the round checks the child
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert: the persisted child, byte for byte, with the handed-over commitment as one package
        var (parent, packaged) = Assert.Single(_chain.Packages);
        Assert.Equal(peer.GetHash(), parent.GetHash());
        Assert.Equal(peer.ToBytes(), parent.ToBytes());
        Assert.Equal(Load(child).GetHash(), packaged.GetHash());
        Assert.Equal(child.RawTransaction, packaged.ToBytes());
        Assert.Equal(BroadcastState.Pending, child.State);
    }

    [Fact]
    public async Task Given_TheHandOver_When_Round_Then_ThePeerCommitmentIsStoredWithItsBytes()
    {
        // Arrange: the mempool reactor handed Bob's commitment over (NL-390)
        var peer = PeerCommitmentInMempool();

        // Act
        Service.OnPeerCommitmentInMempool(_channel.ChannelId, ToSigned(peer), false);
        await Service.WhenIdleAsync();

        // Assert: a pending PeerCommitment row holds the bytes for a restart
        var row = Assert.Single(_store.Rows.Where(r => r.Purpose == BroadcastPurpose.PeerCommitment));
        Assert.Equal(_channel.ChannelId, row.ChannelId);
        Assert.Equal(new TxId(peer.GetHash().ToBytes()), row.TransactionId);
        Assert.Equal(peer.ToBytes(), row.RawTransaction);
        Assert.Equal(BroadcastState.Pending, row.State);
    }

    [Fact]
    public async Task Given_ARestartAfterTheHandOver_When_BitcoindDoesNotHaveTheCommitment_Then_TheStoredBytesKeepTheBump()
    {
        // Arrange: the hand-over row exists (and the first child); after the restart bitcoind does not have Bob's
        // commitment, so only the row's bytes are left
        var peer = PeerCommitmentInMempool();
        Service.OnPeerCommitmentInMempool(_channel.ChannelId, ToSigned(peer), false);
        await Service.WhenIdleAsync();
        var child = Assert.Single(_store.Children);
        _chain.Transactions.Remove(peer.GetHash());
        var restarted = BuildProvider().GetRequiredService<AnchorCpfpService>();
        _monitor.Setup(m => m.PublishAsync(It.Is<BroadcastTransactionModel>(b => b.Purpose
                                                                                == BroadcastPurpose.AnchorCpfp)))
                .Callback<BroadcastTransactionModel>(_published.Add)
                .ReturnsAsync(false);
        _chain.PackageAnswer = _chain.AcceptPackage;

        // Act: the restarted service loads the hand-over row with the channel's first round (the channel is Open)
        await restarted.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert: the stored bytes and the persisted child, byte for byte, went in as one package
        var (parent, packaged) = Assert.Single(_chain.Packages);
        Assert.Equal(peer.GetHash(), parent.GetHash());
        Assert.Equal(peer.ToBytes(), parent.ToBytes());
        Assert.Equal(Load(child).GetHash(), packaged.GetHash());
        Assert.Equal(child.RawTransaction, packaged.ToBytes());
        Assert.Equal(BroadcastState.Pending, child.State);
    }

    [Fact]
    public async Task Given_TheHandOverNeverEntersTheMempool_When_MissingForTheGraceBlocks_Then_ItIsDroppedAndReleased()
    {
        // Arrange: the handed-over commitment never entered bitcoind (below its minimum) and our packages are refused
        // for a missing package relay: the trust in the hand-over is bounded (NL-390)
        var peer = PeerCommitmentInMempool();
        Service.OnPeerCommitmentInMempool(_channel.ChannelId, ToSigned(peer), false);
        await Service.WhenIdleAsync();
        var child = Assert.Single(_store.Children);
        var handOver = Assert.Single(_store.Rows.Where(r => r.Purpose == BroadcastPurpose.PeerCommitment));
        _chain.Transactions.Remove(peer.GetHash());

        // Act: five blocks without it (the monitor is at bitcoind's tip; the deadline keeps the RBF replacing it),
        // then the sixth
        for (uint height = 501; height <= 505; height++)
        {
            await Service.RunOnceAsync(height, TestContext.Current.CancellationToken);
            child = _store.Children.Single(c => c.State == BroadcastState.Pending);
        }

        await Service.RunOnceAsync(506, TestContext.Current.CancellationToken);

        // Assert: the child and the hand-over row are abandoned, the wallet inputs released
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(BroadcastState.Abandoned, handOver.State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_OurCommitmentCloseRecorded_When_Round_Then_TheHandOverRowIsAbandoned()
    {
        // Arrange: Bob's commitment was handed over (its row is pending), but ours confirmed instead
        var peer = PeerCommitmentInMempool();
        var ours = StoreOurCommitment();
        Service.OnPeerCommitmentInMempool(_channel.ChannelId, ToSigned(peer), false);
        await Service.WhenIdleAsync();
        var child = Assert.Single(_store.Children);
        var handOver = Assert.Single(_store.Rows.Where(r => r.Purpose == BroadcastPurpose.PeerCommitment));
        ours.MarkConfirmed(505, new Hash(new byte[32]));
        _close = new ChannelCloseModel(_channel.ChannelId, ChannelCloseKind.LocalCommitment, ours.TransactionId,
                                       ours.CommitmentNumber, 505, new Hash(new byte[32]), DateTimeOffset.UtcNow);
        _channel.UpdateState(ChannelState.OnchainResolving);

        // Act
        await Service.RunOnceAsync(506, TestContext.Current.CancellationToken);

        // Assert: the peer children and the hand-over row are abandoned (the funding output is spent)
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(BroadcastState.Abandoned, handOver.State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_FailedChannelAfterARestart_When_PeerCommitmentIsInTheMempool_Then_FoundByItsTxidAndBumped()
    {
        // Arrange: no hand-over (the node restarted after the mempool saw it); our HTLC's deadline made us fail the
        // channel, and bitcoind has Bob's current commitment
        var peer = PeerCommitmentInMempool();
        _channel.UpdateState(ChannelState.Failed);

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert: its txid is the one we rebuild from the snapshot, and it gets the child
        var child = Load(Assert.Single(_store.Children));
        Assert.Equal(peer.GetHash(), child.Inputs[0].PrevOut.Hash);
        AnchorTx.AssertScriptsValid(child, peer, _wallet);
    }

    [Fact]
    public async Task Given_OurCommitmentRefusedBecauseThePeersIsInTheMempool_When_Round_Then_OnlyThePeersIsBumped()
    {
        // Arrange: our commitment row is pending (the peer's holds the funding output) and below the estimate too
        var peer = PeerCommitmentInMempool();
        var ours = StoreOurCommitment();

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);

        // Assert: one child, of the peer's commitment; ours waits (no child, no package)
        var child = Load(Assert.Single(_store.Children));
        Assert.Equal(peer.GetHash(), child.Inputs[0].PrevOut.Hash);
        Assert.NotEqual(Load(ours).GetHash(), child.Inputs[0].PrevOut.Hash);
        Assert.Empty(_chain.Packages);
    }

    [Fact]
    public async Task Given_PendingPeerChild_When_EstimateRisesAndBumpIsDue_Then_ReplacedWithBip125Fee()
    {
        // Arrange
        var peer = PeerCommitmentInMempool();
        _channel.UpdateState(ChannelState.Failed);
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var first = Assert.Single(_store.Children);
        _estimate = 20_000;

        // Act: not due one block later (RbfIntervalBlocks 2), due two blocks later
        await Service.RunOnceAsync(501, TestContext.Current.CancellationToken);
        var afterOneBlock = _store.Children.Count;
        await Service.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, afterOneBlock);
        Assert.Equal(BroadcastState.Replaced, first.State);
        var replacement = _store.Children.Single(c => c.TransactionId != first.TransactionId);
        Assert.Equal(first.TransactionId, replacement.ReplacesTransactionId);
        var oldChild = Load(first);
        var newChild = Load(replacement);
        Assert.Equal(oldChild.Inputs[0].PrevOut, newChild.Inputs[0].PrevOut);
        AnchorTx.AssertScriptsValid(newChild, peer, _wallet);
        var oldFee = AnchorTx.ChildFee(oldChild, _wallet);
        var newFee = AnchorTx.ChildFee(newChild, _wallet);
        Assert.True(newFee >= oldFee * 5 / 4, $"{newFee} < 1.25 x {oldFee}");
        Assert.True(newFee >= oldFee + (ulong)newChild.GetVirtualSize(), $"{newFee} below the relay increment");
        Assert.True(PackageFeerate(peer, newChild) >= 20_000);
        Assert.Equal([first, replacement], _published);
    }

    [Fact]
    public async Task Given_OpenChannelWithAPendingPeerChildAfterARestart_When_BumpIsDue_Then_ChildReplaced()
    {
        // Arrange: the mempool reactor handed Bob's commitment over while the channel is Open and our child went out;
        // then the node restarted: the hand-over is gone (memory only), the channel is still Open, and bitcoind does not
        // report the mempool transaction again
        var peer = PeerCommitmentInMempool();
        Service.OnPeerCommitmentInMempool(_channel.ChannelId, ToSigned(peer), false);
        await Service.WhenIdleAsync();
        var first = Assert.Single(_store.Children);
        var restarted = BuildProvider().GetRequiredService<AnchorCpfpService>();
        _estimate = 20_000;

        // Act: the bump is due two blocks after the child
        await restarted.RunOnceAsync(502, TestContext.Current.CancellationToken);

        // Assert: the persisted peer child keeps the Open channel in the rounds; it is replaced over Bob's commitment
        Assert.Equal(ChannelState.Open, _channel.State);
        Assert.Equal(BroadcastState.Replaced, first.State);
        var replacement = _store.Children.Single(c => c.TransactionId != first.TransactionId);
        Assert.Equal(first.TransactionId, replacement.ReplacesTransactionId);
        AnchorTx.AssertScriptsValid(Load(replacement), peer, _wallet);
    }

    [Fact]
    public async Task Given_ReservationWithoutItsChildRowAndPeerCommitmentConfirmed_When_Rounds_Then_ReleasedOnce()
    {
        // Arrange: a crash between the reservation's save and the first child's: inputs reserved, no child row, no
        // LocalCommitment row; then Bob's commitment confirms and the watcher records the close
        var peer = PeerCommitmentInMempool();
        await _wallet.ReserveAsync(_channel.ChannelId, 10_000, 2_500, TestContext.Current.CancellationToken);
        Assert.NotEmpty(_wallet.Reserved(_channel.ChannelId));
        Confirm(peer, 505);

        // Act
        await Service.RunOnceAsync(506, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(507, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_store.Children);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_PeerCommitmentConfirmedWithoutAnyReservation_When_Rounds_Then_NothingReleased()
    {
        // Arrange
        var peer = PeerCommitmentInMempool();
        Confirm(peer, 505);

        // Act
        await Service.RunOnceAsync(506, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_PeerCommitmentWithoutAnUntrimmedHtlc_When_Round_Then_NoChild()
    {
        // Arrange: the HTLC is below Bob's dust limit on his commitment: no deadline, the peer's to pay for
        PeerCommitmentInMempool(htlcMsat: 500_000);
        _channel.UpdateState(ChannelState.Failed);

        // Act
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_store.Children);
        Assert.Empty(_wallet.Reserved(_channel.ChannelId));
    }

    [Fact]
    public async Task Given_PeerCommitmentConfirmedWithOurChildPending_When_Rounds_Then_InputsKeptUntilTheAnchorIsSpent()
    {
        // Arrange: our child of Bob's commitment, then Bob's commitment confirms (the watcher recorded the close)
        var peer = PeerCommitmentInMempool();
        _channel.UpdateState(ChannelState.Failed);
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        Confirm(peer, 505);

        // Act 1: our anchor on it still unspent: the child may still confirm
        await Service.RunOnceAsync(506, TestContext.Current.CancellationToken);
        var releasedWhileUnspent = _wallet.ReleaseCount;

        // Act 2: our anchor spent on chain (by another replacement or someone's sweep), seen at bitcoind's tip 507;
        // the next round at 507 knows the monitor processed that block
        _chain.Spent.Add(new OutPoint(peer.GetHash(), OurAnchorOn(peer)));
        _chain.Tip = 507;
        await Service.RunOnceAsync(507, TestContext.Current.CancellationToken);
        var releasedWhenSeen = _wallet.ReleaseCount;
        await Service.RunOnceAsync(507, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, releasedWhileUnspent);
        Assert.Equal(0, releasedWhenSeen);
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_PeerChildConfirmedWithItsCommitment_When_Round_Then_InputsReleasedOnce()
    {
        // Arrange
        var peer = PeerCommitmentInMempool();
        _channel.UpdateState(ChannelState.Failed);
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        Confirm(peer, 505);
        child.MarkConfirmed(505, new Hash(new byte[32]));

        // Act
        await Service.RunOnceAsync(506, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(507, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BroadcastState.Confirmed, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_OurCommitmentConfirmedInstead_When_Round_Then_PeerChildrenAbandonedAndInputsReleased()
    {
        // Arrange: a child of Bob's commitment, then ours confirms (Bob's was evicted and ours got in)
        PeerCommitmentInMempool();
        var ours = StoreOurCommitment();
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        ours.MarkConfirmed(505, new Hash(new byte[32]));
        _close = new ChannelCloseModel(_channel.ChannelId, ChannelCloseKind.LocalCommitment, ours.TransactionId,
                                       ours.CommitmentNumber, 505, new Hash(new byte[32]), DateTimeOffset.UtcNow);
        _channel.UpdateState(ChannelState.OnchainResolving);

        // Act
        await Service.RunOnceAsync(506, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_PeerCommitmentLeftTheMempool_When_MissingForTheGraceBlocks_Then_ChildAbandonedAndReleased()
    {
        // Arrange
        var peer = PeerCommitmentInMempool();
        _channel.UpdateState(ChannelState.Failed);
        await Service.RunOnceAsync(500, TestContext.Current.CancellationToken);
        var child = Assert.Single(_store.Children);
        _chain.Transactions.Remove(peer.GetHash());

        // Act: five blocks without it (monitor at bitcoind's tip), then the sixth
        for (uint height = 501; height <= 505; height++)
            await Service.RunOnceAsync(height, TestContext.Current.CancellationToken);
        var stateAfterFive = child.State;
        await Service.RunOnceAsync(506, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BroadcastState.Pending, stateAfterFive);
        Assert.Equal(BroadcastState.Abandoned, child.State);
        Assert.Equal(1, _wallet.ReleaseCount);
    }

    [Fact]
    public async Task Given_PeerCommitmentConfirmed16BlocksAgo_When_FeesAreLow_Then_BothAnchorsSweptOnce()
    {
        // Arrange: Bob's commitment confirmed at 510 (no child of ours was needed); the block serves it
        var peer = PeerCommitmentInMempool();
        Confirm(peer, 510);
        var block = Network.Main.Consensus.ConsensusFactory.CreateBlock();
        block.Transactions.Add(peer);
        _chain.Blocks[510] = block;
        _estimate = 253;

        // Act
        await Service.RunOnceAsync(524, TestContext.Current.CancellationToken);
        var beforeDue = _sweeps.Count;
        await Service.RunOnceAsync(525, TestContext.Current.CancellationToken);
        await Service.RunOnceAsync(526, TestContext.Current.CancellationToken);

        // Assert: both anchors of Bob's commitment, anyone-can-spend after 16 blocks, script-valid
        Assert.Equal(0, beforeDue);
        var sweep = Transaction.Load(Assert.Single(_sweeps).RawTxBytes, Network.Main);
        Assert.Equal(2, sweep.Inputs.Count);
        Assert.All(sweep.Inputs, i => Assert.Equal(peer.GetHash(), i.PrevOut.Hash));
        Assert.Contains(sweep.Inputs, i => i.PrevOut.N == OurAnchorOn(peer));
        for (var i = 0; i < sweep.Inputs.Count; i++)
        {
            Assert.Equal(16u, (uint)sweep.Inputs[i].Sequence);
            Assert.True(sweep.Inputs.AsIndexedInputs().ElementAt(i)
                             .VerifyScript(peer.Outputs[sweep.Inputs[i].PrevOut.N], out var error), $"{error}");
        }
    }

    public void Dispose()
    {
        foreach (var provider in _providers)
            provider.Dispose();
        _pair.Dispose();
    }

    /// <summary>A node's service graph over the shared store, wallet and chain.</summary>
    private ServiceProvider BuildProvider()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.BroadcastTransactionDbRepository).Returns(_store);
        unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(_resolutions.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            _store.Saves++;
            return Task.CompletedTask;
        });

        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => LightningMoney.Satoshis(_estimate));
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(() => LightningMoney.Satoshis(_estimate));
        var destination = new Mock<ISweepDestinationProvider>();
        destination.Setup(d => d.GetDestinationScriptAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_walletScript);

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IUtxoMemoryRepository>().Object);
        services.AddBitcoinInfrastructure();
        services.AddSingleton<IBitcoinChainService>(_chain);
        services.AddSingleton(_signer);
        services.AddSingleton<ICommitmentTransactionModelFactory, CommitmentTransactionModelFactory>();
        services.AddOnchainBitcoinServices();
        services.AddSingleton(_monitor.Object);
        services.AddSingleton(_memory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton(feeService.Object);
        services.AddSingleton(destination.Object);
        services.AddSingleton<IAnchorFeeInputSource>(_wallet);
        services.AddSingleton(new SweepFeePolicy());
        services.AddScoped(_ => unitOfWork.Object);
        services.AddAnchorCpfpServices();
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return provider;
    }

    /// <summary>
    /// Alice offers an HTLC (expiry 600; 20,000 sat unless given, below Bob's 600 sat dust limit it is trimmed on his
    /// commitment), the dance settles, and Bob's fully signed current commitment is in bitcoind's mempool.
    /// </summary>
    private Transaction PeerCommitmentInMempool(ulong htlcMsat = 20_000_000)
    {
        _pair.Add(_pair.Alice, htlcMsat, RealSigningCommitmentPair.Preimage(1), HtlcExpiry);
        _pair.Settle(_pair.Alice);
        _channel.UpdateCommitments(_pair.Alice.State);
        _pair.Bob.Channel.UpdateCommitments(_pair.Bob.State);

        var bobFactory = new CommitmentTransactionModelFactory(
            new CommitmentKeyDerivationService(_provider.GetRequiredService<IKeyDerivationService>(), _pair.Bob.Signer),
            _pair.Bob.Signer);
        var builder = new LocalCommitmentBroadcastBuilder(bobFactory,
                                                          _provider.GetRequiredService<ICommitmentTransactionBuilder>(),
                                                          _pair.Bob.Signer);
        var peer = Transaction.Load(builder.Build(_pair.Bob.Channel).Transaction.RawTxBytes, Network.Main);
        _chain.Transactions[peer.GetHash()] = peer;
        return peer;
    }

    /// <summary>Our own (Alice's) commitment, failed and stored for broadcast.</summary>
    private BroadcastTransactionModel StoreOurCommitment()
    {
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

    /// <summary>The watcher recorded Bob's commitment confirmed at <paramref name="height"/>.</summary>
    private void Confirm(Transaction peer, uint height)
    {
        _close = new ChannelCloseModel(_channel.ChannelId, ChannelCloseKind.RemoteCommitment,
                                       new TxId(peer.GetHash().ToBytes()), _pair.Alice.State.RemoteCommit.Number,
                                       height, new Hash(new byte[32]), DateTimeOffset.UtcNow);
        if (_channel.State != ChannelState.Failed)
            _channel.UpdateState(ChannelState.Failed);
        _channel.UpdateState(ChannelState.OnchainResolving);
    }

    private uint OurAnchorOn(Transaction commitment) =>
        new AnchorChildTransactionBuilder().FindAnchorOutput(commitment.ToBytes(),
                                                             _channel.LocalKeySet.FundingCompactPubKey)
     ?? throw new InvalidOperationException("No anchor of ours");

    private ulong PackageFeerate(Transaction commitment, Transaction child)
    {
        var commitmentFee = RealSigningCommitmentPair.FundingSatoshis - AnchorTx.OutputsSat(commitment);
        var childFee = AnchorTx.ChildFee(child, _wallet);
        return (commitmentFee + childFee) * 1000
             / (ulong)(AnchorTx.Weight(commitment) + AnchorTx.Weight(child));
    }

    private static SignedTransaction ToSigned(Transaction tx) => new(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());

    private static Transaction Load(BroadcastTransactionModel row) => Transaction.Load(row.RawTransaction, Network.Main);
}
