using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Application.Protocol.Factories;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
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
    private int _saves;

    public void Dispose()
    {
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
        Assert.Equal("no answer", peer.Error);
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

    private async Task<byte[]> ExportAsync(CancellationToken ct, params (byte Tag, bool Anchors)[] channels)
    {
        // The same node key (seed 7) and channel key derivation as _node
        var source = new BackupTestData();
        foreach (var (tag, anchors) in channels)
            source.AddChannel(tag, anchors: anchors);
        return (await source.CreateService().ExportAsync(null, ct)).Backup;
    }

    private ChannelRestoreService CreateService(bool failSave = false)
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

        return new ChannelRestoreService(_node.CreateService(provider), _channelManager.Object,
                                         new MessageFactory(Options.Create(new NodeOptions())), serializer.Object,
                                         Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                         watcher.Object, _peerManager.Object, _node.KeyManager,
                                         provider.GetRequiredService<IServiceScopeFactory>(), _sha256,
                                         _node.Signer.Object, NullLogger<ChannelRestoreService>.Instance)
        {
            DisconnectTimeout = TimeSpan.FromSeconds(1)
        };
    }
}