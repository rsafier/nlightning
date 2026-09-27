using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Application.Channels.Backup.Models;
using Application.Gossip.Graph.Interfaces;
using Application.Onchain.Interfaces;
using Application.Protocol.Factories;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Domain.Serialization.Interfaces;
using Infrastructure.Crypto.Hashes;

public class ChannelRestoreServiceTests : IDisposable
{
    private readonly BackupTestData _node = new();
    private readonly List<ChannelModel> _storedChannels = [];
    private readonly List<PeerModel> _storedPeers = [];
    private readonly List<WatchedOutpointModel> _storedWatches = [];
    private readonly List<ChannelModel> _registered = [];
    private readonly List<WatchedOutpointModel> _tracked = [];
    private readonly List<string> _connects = [];
    private readonly List<string> _calls = [];
    private readonly Mock<IPeerManager> _peerManager = new();
    private readonly Mock<IChannelManager> _channelManager = new();
    private readonly Sha256 _sha256 = new();
    private readonly List<(TxId TxId, uint Index, TxId Spender, uint Height)> _markedSpent = [];
    private readonly List<ChannelRestoreService> _services = [];
    private int _saves;

    public void Dispose()
    {
        foreach (var service in _services)
            service.Dispose();
        _sha256.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Given_AnEmptyDatabase_When_Restored_Then_RecoveryChannelsStoredRegisteredWatchedAndPeersConnected()
    {
        // Arrange: a backup of an anchors and a static_remotekey channel made before the database was lost
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, true), (2, false));
        var service = CreateService();

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Equal(2, result.RestoredCount);
        Assert.All(result.Channels, c => Assert.Equal(ChannelRestoreAction.Restore, c.Action));
        Assert.Equal(2, _storedChannels.Count);
        Assert.All(_storedChannels, c =>
        {
            Assert.True(RecoveryChannels.IsRecoveryChannel(c));
            Assert.NotNull(c.ErrorSent);
        });
        Assert.Equal([true, false], _storedChannels.Select(c => c.ChannelParams.OptionAnchorOutputs));

        // The peer row from the backup's address, the funding output watched, one save per channel
        Assert.Equal(2, _storedPeers.Count);
        Assert.Equal("10.0.0.1", _storedPeers[0].Host);
        Assert.Equal(9736u, _storedPeers[0].Port);
        Assert.Equal(2, _storedWatches.Count);
        Assert.All(_storedWatches, w => Assert.Equal(WatchedOutpointPurpose.FundingOutput, w.Purpose));
        Assert.Equal(_storedChannels.Select(c => c.FundingOutput!.TransactionId!.Value),
                     _storedWatches.Select(w => w.TransactionId));
        Assert.Equal(2, _saves);

        // Registered after the save, watch followed, then each peer connected (the connection sends the reestablish)
        Assert.Equal(_storedChannels.Select(c => c.ChannelId), _registered.Select(c => c.ChannelId));
        Assert.Equal(_storedWatches, _tracked);
        Assert.Equal(["save", "register", "save", "register", "connect", "connect"], _calls);
        Assert.Equal(2, result.Peers.Count);
        Assert.All(result.Peers, p => Assert.True(p.Connected));
        Assert.Equal($"{_storedChannels[0].RemoteNodeId}@10.0.0.1:9736", _connects[0]);
    }

    [Fact]
    public async Task Given_AChannelAlreadyInTheDatabase_When_Restored_Then_LeftAsItIsAndOnlyTheOtherRestored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false), (2, false));
        var live = new BackupTestData().AddChannel(1);
        _storedChannels.Add(live);
        var service = CreateService();

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Equal(ChannelRestoreAction.AlreadyExists, result.Channels[0].Action);
        Assert.Contains("Open", result.Channels[0].Detail);
        Assert.Equal(ChannelRestoreAction.Restore, result.Channels[1].Action);
        Assert.Equal(2, _storedChannels.Count);
        Assert.Same(live, _storedChannels[0]);
        Assert.Single(_registered);
        Assert.Single(_connects);
    }

    [Fact]
    public async Task Given_ATamperedBackup_When_Restored_Then_RefusedAndNothingStored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        backup[^5] ^= 0x01;
        var service = CreateService();

        // Act / Assert
        await Assert.ThrowsAnyAsync<ChannelBackupException>(() => service.RestoreAsync(backup, ct));
        Assert.Empty(_storedChannels);
        Assert.Empty(_connects);
    }

    [Fact]
    public async Task Given_TheBackupOfAnotherNode_When_Restored_Then_Refused()
    {
        // Arrange: encrypted to another node key, so it does not decrypt with ours
        var ct = TestContext.Current.CancellationToken;
        var other = new BackupTestData(nodeSeed: 9);
        other.AddChannel(1);
        var backup = (await other.CreateService().ExportAsync(null, ct)).Backup;
        var service = CreateService();

        // Act / Assert
        await Assert.ThrowsAnyAsync<ChannelBackupException>(() => service.RestoreAsync(backup, ct));
        Assert.Empty(_storedChannels);
    }

    [Fact]
    public async Task Given_ABackupOfAnotherChain_When_Restored_Then_Refused()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var source = new BackupTestData { Network = BitcoinNetwork.Signet };
        source.AddChannel(1);
        var backup = (await source.CreateService().ExportAsync(null, ct)).Backup;
        var service = CreateService();

        // Act
        var e = await Assert.ThrowsAsync<ChannelBackupException>(() => service.RestoreAsync(backup, ct));

        // Assert
        Assert.Contains("another chain", e.Message);
        Assert.Empty(_storedChannels);
    }

    [Fact]
    public async Task Given_TheKeyFileDerivesOtherKeys_When_Restored_Then_ChannelSkippedAndPeerNotAsked()
    {
        // Arrange: same node key, but the channel keys come from another seed
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        _node.Signer.Setup(s => s.GetChannelBasepoints(It.IsAny<uint>()))
             .Returns(new ChannelBasepoints(BackupTestData.Key(0x02, 99, 1), BackupTestData.Key(0x02, 99, 2),
                                            BackupTestData.Key(0x02, 99, 3), BackupTestData.Key(0x02, 99, 4),
                                            BackupTestData.Key(0x02, 99, 5)));
        var service = CreateService();

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Equal(ChannelRestoreAction.KeysMismatch, Assert.Single(result.Channels).Action);
        Assert.Empty(_storedChannels);
        Assert.Empty(result.Peers);
        Assert.Empty(_connects);
    }

    [Fact]
    public async Task Given_ThePeerIsConnected_When_Restored_Then_DisconnectedThenConnectedAgain()
    {
        // Arrange: the peer (which still has the channel) connected to our wiped node first
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var connected = true;
        _peerManager.Setup(p => p.GetPeer(It.IsAny<CompactPubKey>()))
                    .Returns((CompactPubKey id) => connected ? new PeerModel(id, "10.0.0.1", 9736, "IPv4") : null);
        _peerManager.Setup(p => p.DisconnectPeer(It.IsAny<CompactPubKey>(), It.IsAny<Exception?>()))
                    .Callback(() =>
                     {
                         _calls.Add("disconnect");
                         connected = false;
                     });
        var service = CreateService();

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Equal(["save", "register", "disconnect", "connect"], _calls);
        Assert.True(Assert.Single(result.Peers).Connected);
    }

    [Fact]
    public async Task Given_ThePeerIsUnreachable_When_Restored_Then_ChannelKeptAndThePeerReported()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var service = CreateService();
        _peerManager.Setup(p => p.ConnectToPeerAsync(It.IsAny<PeerAddressInfo>()))
                    .ThrowsAsync(new TimeoutException("no answer"));

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Equal(ChannelRestoreAction.Restore, Assert.Single(result.Channels).Action);
        var peer = Assert.Single(result.Peers);
        Assert.False(peer.Connected);
        Assert.Contains("no answer", peer.Error);
        Assert.Contains("retried in the background", peer.Error);
        Assert.Single(_registered);
    }

    [Fact]
    public async Task Given_TheSaveFails_When_Restored_Then_ChannelFailedAndNotRegistered()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var service = CreateService(failSave: true);

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        var channel = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Failed, channel.Action);
        Assert.Empty(_registered);
        Assert.Empty(result.Peers);
    }

    [Fact]
    public async Task Given_TheFundingSpentBeforeTheRestore_When_Restored_Then_TheSpendIsMarkedAndHandedToTheOnchainWatcher()
    {
        // Arrange: the peer force-closed (on our wiped node's error) before restorechanbackup ran
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, true));
        var spendingTxId = new TxId(Enumerable.Repeat((byte)0x5E, 32).ToArray());
        OutpointSpentEventArgs? located = null;
        var locator = new Mock<IFundingSpendLocator>();
        locator.Setup(l => l.LocateAsync(It.IsAny<ChannelBackupEntry>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((ChannelBackupEntry entry, CancellationToken _) =>
                {
                    located = new OutpointSpentEventArgs(entry.ChannelId,
                                                         new SignedTransaction(spendingTxId, [0x02, 0x00]), 950, 3,
                                                         entry.FundingTxId, entry.FundingOutputIndex,
                                                         new Hash(new byte[32]));
                    return new FundingSpendLocation(FundingSpendStatus.SpentFound, located);
                });
        var watcher = new Mock<IOnchainChannelWatcher>();
        watcher.Setup(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                     It.IsAny<CancellationToken>()))
               .Callback(() => _calls.Add("onchain"))
               .ReturnsAsync((FundingSpendOutcome?)null);
        var service = CreateService(spendLocator: locator.Object, onchainWatcher: watcher.Object);

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert: registered first, then the spend recorded on the funding watch and handed over
        var channel = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, channel.Action);
        Assert.Contains("already closed", channel.Detail);
        Assert.Contains("950", channel.Detail);
        watcher.Verify(w => w.HandleFundingSpentAsync(located!, It.IsAny<CancellationToken>()), Times.Once);
        var marked = Assert.Single(_markedSpent);
        Assert.Equal(_storedWatches[0].TransactionId, marked.TxId);
        Assert.Equal(_storedWatches[0].OutputIndex, marked.Index);
        Assert.Equal(spendingTxId, marked.Spender);
        Assert.Equal(950u, marked.Height);
        Assert.Equal(["save", "register", "save", "onchain", "connect"], _calls);
    }

    [Fact]
    public async Task Given_ASpentFundingWhoseSpendIsNotFound_When_Restored_Then_TheResultSaysFromWhereToRescan()
    {
        // Arrange: spent, but not in the searched blocks (from 5000 on); the funding was created at height 101
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var locator = new Mock<IFundingSpendLocator>();
        locator.Setup(l => l.LocateAsync(It.IsAny<ChannelBackupEntry>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FundingSpendLocation(FundingSpendStatus.SpentNotFound, SearchedFromHeight: 5_000,
                                                      FloorHeight: 5_000));
        var watcher = new Mock<IOnchainChannelWatcher>();
        var service = CreateService(spendLocator: locator.Object, onchainWatcher: watcher.Object);

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        var channel = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, channel.Action);
        Assert.StartsWith("FundingAlreadySpent", channel.Detail);
        Assert.Contains("rescan from height 101", channel.Detail);
        watcher.VerifyNoOtherCalls();
        Assert.Empty(_markedSpent);
    }

    [Fact]
    public async Task Given_AnUnspentFunding_When_Restored_Then_NothingIsHandedToTheOnchainWatcher()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var locator = new Mock<IFundingSpendLocator>();
        locator.Setup(l => l.LocateAsync(It.IsAny<ChannelBackupEntry>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FundingSpendLocation(FundingSpendStatus.Unspent));
        var watcher = new Mock<IOnchainChannelWatcher>();
        var service = CreateService(spendLocator: locator.Object, onchainWatcher: watcher.Object);

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Contains("asked to force close", Assert.Single(result.Channels).Detail);
        watcher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_RestoredChannels_When_Restored_Then_TheKeyIndexIsReservedPastTheHighestBeforeAnythingIsStored()
    {
        // Arrange: key indexes 1, 7 and 4; channel 4 is already in the database (not restored)
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false), (7, true), (4, false));
        _storedChannels.Add(new BackupTestData().AddChannel(4));
        var reserver = new Mock<IChannelKeyIndexReserver>();
        reserver.Setup(r => r.ReserveThroughAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                .Callback(() => _calls.Add("reserve"))
                .ReturnsAsync(7u);
        var service = CreateService(keyIndexReserver: reserver.Object);

        // Act
        await service.RestoreAsync(backup, ct);

        // Assert
        reserver.Verify(r => r.ReserveThroughAsync(7u, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("reserve", _calls[0]);
    }

    [Fact]
    public async Task Given_NothingToRestore_When_Restored_Then_NoKeyIndexIsReserved()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        _storedChannels.Add(new BackupTestData().AddChannel(1));
        var reserver = new Mock<IChannelKeyIndexReserver>();
        var service = CreateService(keyIndexReserver: reserver.Object);

        // Act
        await service.RestoreAsync(backup, ct);

        // Assert
        reserver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_APeerThatDoesNotDisconnect_When_Restored_Then_ReportedNotConnectedAndNoConnectTried()
    {
        // Arrange: the old connection never closes within the timeout
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        _peerManager.Setup(p => p.GetPeer(It.IsAny<CompactPubKey>()))
                    .Returns((CompactPubKey id) => new PeerModel(id, "10.0.0.1", 9736, "IPv4"));
        var service = CreateService();

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        var peer = Assert.Single(result.Peers);
        Assert.False(peer.Connected);
        Assert.Contains("did not close", peer.Error);
        Assert.Empty(_connects);
        Assert.Equal(ChannelRestoreAction.Restore, Assert.Single(result.Channels).Action);
    }

    [Fact]
    public async Task Given_ASpendOlderThanTheSearchDepth_When_Restored_Then_TheBackgroundRescanFindsItAndHandsItOver()
    {
        // Arrange: not in the recent blocks (5000 to the tip), found below them by the rescan (NL-430)
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, true));
        var spendingTxId = new TxId(Enumerable.Repeat((byte)0x6E, 32).ToArray());
        var locator = new Mock<IFundingSpendLocator>();
        locator.Setup(l => l.LocateAsync(It.IsAny<ChannelBackupEntry>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FundingSpendLocation(FundingSpendStatus.SpentNotFound, SearchedFromHeight: 5_000,
                                                      FloorHeight: 900));
        OutpointSpentEventArgs? located = null;
        locator.Setup(l => l.RescanAsync(It.IsAny<ChannelBackupEntry>(), 5_000u, It.IsAny<CancellationToken>()))
               .ReturnsAsync((ChannelBackupEntry entry, uint _, CancellationToken _) =>
                {
                    located = new OutpointSpentEventArgs(entry.ChannelId,
                                                         new SignedTransaction(spendingTxId, [0x02, 0x00]), 950, 1,
                                                         entry.FundingTxId, entry.FundingOutputIndex,
                                                         new Hash(new byte[32]));
                    return new FundingSpendLocation(FundingSpendStatus.SpentFound, located);
                });
        var watcher = new Mock<IOnchainChannelWatcher>();
        var service = CreateService(spendLocator: locator.Object, onchainWatcher: watcher.Object);

        // Act
        var result = await service.RestoreAsync(backup, ct);
        await service.WaitForBackgroundWorkAsync();

        // Assert: the result says a background search runs; it found the spend, marked the watch, handed it over
        var channel = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, channel.Action);
        Assert.StartsWith("FundingSpendRescan", channel.Detail);
        Assert.Contains("4999 down to 900", channel.Detail);
        watcher.Verify(w => w.HandleFundingSpentAsync(located!, It.IsAny<CancellationToken>()), Times.Once);
        var marked = Assert.Single(_markedSpent);
        Assert.Equal(spendingTxId, marked.Spender);
        Assert.Equal(950u, marked.Height);
    }

    [Fact]
    public async Task Given_ARecoveryChannelStillWaiting_When_RestoredAgain_Then_TheSpendIsLookedUpAgainAndTheChannelKept()
    {
        // Arrange: the first restore could not read the chain
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var locator = new Mock<IFundingSpendLocator>();
        locator.Setup(l => l.LocateAsync(It.IsAny<ChannelBackupEntry>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new FundingSpendLocation(FundingSpendStatus.ChainUnavailable, Error: "rpc down"));
        var watcher = new Mock<IOnchainChannelWatcher>();
        var service = CreateService(spendLocator: locator.Object, onchainWatcher: watcher.Object);
        var first = await service.RestoreAsync(backup, ct);
        Assert.Contains("run restorechanbackup again", Assert.Single(first.Channels).Detail);
        locator.Setup(l => l.LocateAsync(It.IsAny<ChannelBackupEntry>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((ChannelBackupEntry entry, CancellationToken _) =>
                                 new FundingSpendLocation(FundingSpendStatus.SpentFound,
                                                          new OutpointSpentEventArgs(
                                                              entry.ChannelId,
                                                              new SignedTransaction(
                                                                  new TxId(Enumerable.Repeat((byte)0x7E, 32)
                                                                                     .ToArray()), [0x02]), 960, 1,
                                                              entry.FundingTxId, entry.FundingOutputIndex,
                                                              new Hash(new byte[32]))));

        // Act
        var second = await service.RestoreAsync(backup, ct);

        // Assert: not stored again, the spend handed over, the peer (not connected) asked again
        var channel = Assert.Single(second.Channels);
        Assert.Equal(ChannelRestoreAction.AlreadyExists, channel.Action);
        Assert.Contains("recovery channel", channel.Detail);
        Assert.Contains("already closed", channel.Detail);
        Assert.Single(_storedChannels);
        Assert.Single(_registered);
        watcher.Verify(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                      It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, _connects.Count);
    }

    [Fact]
    public async Task Given_ALiveChannelAlreadyInTheDatabase_When_Restored_Then_ItsSpendIsNotLookedUp()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        _storedChannels.Add(new BackupTestData().AddChannel(1));
        var locator = new Mock<IFundingSpendLocator>();
        var service = CreateService(spendLocator: locator.Object);

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Equal(ChannelRestoreAction.AlreadyExists, Assert.Single(result.Channels).Action);
        locator.VerifyNoOtherCalls();
        Assert.Empty(result.Peers);
    }

    [Fact]
    public async Task Given_ThePeerAnnouncedAnotherAddress_When_Restored_Then_EveryKnownAddressIsTriedGraphFirst()
    {
        // Arrange: the graph's addresses are tried first and fail; the backup's address answers (NL-431)
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var nodeId = BackupTestData.Key(0x03, 1, 9);
        var graph = BackupTestData.GraphWith(BackupTestData.GraphNodeWith(
                                                 nodeId,
                                                 AddressDescriptor.FromHost(AddressDescriptorType.IPv4, "192.0.2.5",
                                                                            9735),
                                                 AddressDescriptor.FromHost(AddressDescriptorType.IPv6, "2001:db8::1",
                                                                            9735)));
        var service = CreateService(graphStore: graph.Object);
        FailConnectsTo(a => !a.EndsWith("@10.0.0.1:9736", StringComparison.Ordinal));

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        var peer = Assert.Single(result.Peers);
        Assert.True(peer.Connected);
        Assert.Equal("10.0.0.1:9736", peer.Address);
        Assert.Equal([$"{nodeId}@192.0.2.5:9735", $"{nodeId}@[2001:db8::1]:9735", $"{nodeId}@10.0.0.1:9736"],
                     _connects);
    }

    [Fact]
    public async Task Given_NoAddressAnswers_When_TheGraphLearnsANewOne_Then_ThePeerIsReconnectedInTheBackground()
    {
        // Arrange: the backup's address is dead and the graph does not know the peer yet
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var nodeId = BackupTestData.Key(0x03, 1, 9);
        var graph = new Mock<IGraphStore>();
        var service = CreateService(graphStore: graph.Object, reconnectDelay: TimeSpan.FromMilliseconds(20));
        FailConnectsTo(a => !a.EndsWith("@198.51.100.7:9735", StringComparison.Ordinal));

        // Act
        var result = await service.RestoreAsync(backup, ct);
        Assert.False(Assert.Single(result.Peers).Connected);
        var announced = BackupTestData.GraphNodeWith(nodeId, AddressDescriptor.FromHost(AddressDescriptorType.IPv4,
                                                                                        "198.51.100.7", 9735));
        graph.Setup(g => g.TryGetNode(nodeId, out announced)).Returns(true);
        await service.WaitForBackgroundWorkAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert: retried until the announced address answered, then stopped
        string[] connects;
        lock (_connects)
            connects = _connects.ToArray();
        Assert.Equal($"{nodeId}@198.51.100.7:9735", connects[^1]);
        Assert.Contains($"{nodeId}@10.0.0.1:9736", connects);
    }

    [Fact]
    public async Task Given_ThePeerConnectsByItself_When_Reconnecting_Then_TheBackgroundLoopStops()
    {
        // Arrange: nothing answers; the peer then connects to us
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var service = CreateService(reconnectDelay: TimeSpan.FromMilliseconds(20));
        FailConnectsTo(_ => true);

        // Act
        await service.RestoreAsync(backup, ct);
        _peerManager.Setup(p => p.GetPeer(It.IsAny<CompactPubKey>()))
                    .Returns((CompactPubKey id) => new PeerModel(id, "10.0.0.1", 9736, "IPv4"));
        await service.WaitForBackgroundWorkAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert: it ended without connecting anywhere else
        lock (_connects)
            Assert.All(_connects, a => Assert.EndsWith("@10.0.0.1:9736", a));
    }

    private async Task<byte[]> ExportAsync(CancellationToken ct, params (byte Tag, bool Anchors)[] channels)
    {
        // The same node key (seed 7) and channel key derivation as _node
        var source = new BackupTestData();
        foreach (var (tag, anchors) in channels)
            source.AddChannel(tag, anchors: anchors);
        return (await source.CreateService().ExportAsync(null, ct)).Backup;
    }

    private ChannelRestoreService CreateService(bool failSave = false, IFundingSpendLocator? spendLocator = null,
                                                IOnchainChannelWatcher? onchainWatcher = null,
                                                IChannelKeyIndexReserver? keyIndexReserver = null,
                                                IGraphStore? graphStore = null, TimeSpan? reconnectDelay = null)
    {
        var channelRepository = new Mock<IChannelDbRepository>();
        channelRepository.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>()))
                         .ReturnsAsync((ChannelId id) => _storedChannels.FirstOrDefault(c => c.ChannelId == id));
        channelRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _storedChannels.ToList());
        var stagedChannels = new List<ChannelModel>();
        channelRepository.Setup(r => r.AddAsync(It.IsAny<ChannelModel>()))
                         .Callback((ChannelModel c) => stagedChannels.Add(c))
                         .Returns(Task.CompletedTask);
        var peerRepository = new Mock<IPeerDbRepository>();
        peerRepository.Setup(r => r.GetByNodeIdAsync(It.IsAny<CompactPubKey>()))
                      .ReturnsAsync((CompactPubKey id) => _storedPeers.FirstOrDefault(p => p.NodeId == id));
        var stagedPeers = new List<PeerModel>();
        peerRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>()))
                      .Callback((PeerModel p) => stagedPeers.Add(p))
                      .Returns(Task.CompletedTask);
        var watchRepository = new Mock<IWatchedOutpointDbRepository>();
        var stagedWatches = new List<WatchedOutpointModel>();
        watchRepository.Setup(r => r.Add(It.IsAny<WatchedOutpointModel>()))
                       .Callback((WatchedOutpointModel w) => stagedWatches.Add(w));
        watchRepository.Setup(r => r.MarkSpentAsync(It.IsAny<TxId>(), It.IsAny<uint>(), It.IsAny<TxId>(),
                                                    It.IsAny<uint>(), It.IsAny<Hash>()))
                       .Callback((TxId txId, uint index, TxId spender, uint height, Hash _) =>
                                     _markedSpent.Add((txId, index, spender, height)))
                       .Returns(Task.CompletedTask);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelRepository.Object);
        unitOfWork.SetupGet(u => u.PeerDbRepository).Returns(peerRepository.Object);
        unitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(watchRepository.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
        {
            if (failSave)
                throw new InvalidOperationException("disk full");

            _saves++;
            _calls.Add("save");
            _storedChannels.AddRange(stagedChannels);
            _storedPeers.AddRange(stagedPeers);
            _storedWatches.AddRange(stagedWatches);
            stagedChannels.Clear();
            stagedPeers.Clear();
            stagedWatches.Clear();
            return Task.CompletedTask;
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        var provider = services.BuildServiceProvider();

        _channelManager.Setup(m => m.RegisterExistingChannelAsync(It.IsAny<ChannelModel>()))
                       .Callback((ChannelModel c) =>
                        {
                            _calls.Add("register");
                            _registered.Add(c);
                        })
                       .Returns(Task.CompletedTask);
        _peerManager.Setup(p => p.ConnectToPeerAsync(It.IsAny<PeerAddressInfo>()))
                    .Callback((PeerAddressInfo a) =>
                     {
                         _calls.Add("connect");
                         _connects.Add(a.Address);
                     })
                    .ReturnsAsync((PeerAddressInfo _) => new PeerModel(BackupTestData.Key(0x03, 1, 9), "h", 1,
                                                                       "IPv4"));
        var watcher = new Mock<IOutpointWatcher>();
        watcher.Setup(w => w.TrackWatchedOutpoint(It.IsAny<WatchedOutpointModel>()))
               .Callback((WatchedOutpointModel w) => _tracked.Add(w));
        var serializer = new Mock<IMessageSerializer>();
        serializer.Setup(s => s.SerializeAsync(It.IsAny<IMessage>(), It.IsAny<Stream>()))
                  .Returns((IMessage _, Stream stream) => stream.WriteAsync(new byte[] { 0x00, 0x11 }).AsTask());

        var service = new ChannelRestoreService(_node.CreateService(provider), _channelManager.Object,
                                         new MessageFactory(Options.Create(new NodeOptions())), serializer.Object,
                                         Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                         watcher.Object, _peerManager.Object, _node.KeyManager,
                                         provider.GetRequiredService<IServiceScopeFactory>(), _sha256,
                                         _node.Signer.Object, NullLogger<ChannelRestoreService>.Instance,
                                         spendLocator, onchainWatcher, keyIndexReserver, graphStore)
        {
            DisconnectTimeout = TimeSpan.FromSeconds(1),
            ReconnectInitialDelay = reconnectDelay ?? TimeSpan.FromMinutes(5),
            ReconnectMaxDelay = reconnectDelay ?? TimeSpan.FromMinutes(5)
        };
        _services.Add(service);
        return service;
    }

    /// <summary>Connection attempts record their address and fail for <paramref name="unreachable"/>.</summary>
    private void FailConnectsTo(Func<string, bool> unreachable)
    {
        _peerManager.Setup(p => p.ConnectToPeerAsync(It.IsAny<PeerAddressInfo>()))
                    .Returns((PeerAddressInfo a) =>
                     {
                         lock (_connects)
                         {
                             _calls.Add("connect");
                             _connects.Add(a.Address);
                         }

                         return unreachable(a.Address)
                                    ? Task.FromException<PeerModel>(new TimeoutException("no answer"))
                                    : Task.FromResult(new PeerModel(BackupTestData.Key(0x03, 1, 9), "h", 1, "IPv4"));
                     });
    }
}