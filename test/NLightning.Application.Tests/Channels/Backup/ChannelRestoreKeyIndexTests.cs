using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.KeyIndexes;
using Domain.Channels.ValueObjects;
using Domain.Node.Fencing;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// NL-1392: a restore claims the backup's key indexes first and fails closed, refuses a channel whose index is in use
/// and holds every new channel key index while it runs.
/// </summary>
public partial class ChannelRestoreServiceTests
{
    private static readonly ExtKey s_channelRoot = ExtKey.CreateFromSeed(Enumerable.Repeat((byte)0x29, 32).ToArray());

    [Fact]
    public async Task Given_AKeyIndexRaiseThatFails_When_Restored_Then_TheRestoreAbortsAndWritesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false), (2, true));
        var reserver = new Mock<IChannelKeyIndexReserver>();
        reserver.Setup(r => r.ReserveThroughAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("key file write failed"));
        var gate = new ChannelKeyIndexGate();
        gate.HoldUntilRestore();
        var service = CreateService(keyIndexReserver: reserver.Object, keyIndexGate: gate);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelBackupException>(() => service.RestoreAsync(backup, ct));

        // Assert: nothing stored, registered, watched or dialed; the start hold stays and the restore's hold ended
        Assert.Contains("Nothing was restored", exception.Message);
        Assert.Contains("NL-1392", exception.Message);
        Assert.Equal(0, _saves);
        Assert.Empty(_storedChannels);
        Assert.Empty(_registered);
        Assert.Empty(_tracked);
        Assert.Empty(_connects);
        Assert.True(gate.IsAwaitingRestore);
        Assert.False(gate.IsRestoreInProgress);
    }

    [Fact]
    public async Task Given_AFencedProcess_When_Restored_Then_TheRestoreAbortsBeforeTheKeyManagerMoves()
    {
        // Arrange: the database's mark is behind the node write fence (NL-1342), so the reserver's raise is refused
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false), (3, false));
        var store = new Mock<IChannelKeyIndexStore>();
        store.Setup(s => s.Raise(It.IsAny<uint>())).Throws(new NodeFencedException("fenced"));
        var keyManager = new Mock<ISecureKeyManager>();
        var reserver = new SecureKeyManagerKeyIndexReserver(keyManager.Object, keyIndexStore: store.Object);
        var service = CreateService(keyIndexReserver: reserver);

        // Act
        var exception = await Assert.ThrowsAsync<ChannelBackupException>(() => service.RestoreAsync(backup, ct));

        // Assert
        Assert.IsType<NodeFencedException>(exception.InnerException);
        store.Verify(s => s.Raise(3u), Times.Once);
        keyManager.Verify(k => k.EnsureLastUsedChannelIndexAtLeast(It.IsAny<uint>()), Times.Never);
        keyManager.Verify(k => k.ReserveChannelKeyIndex(), Times.Never);
        Assert.Equal(0, _saves);
        Assert.Empty(_registered);
    }

    [Fact]
    public async Task Given_ALiveChannelAtABackedUpIndex_When_Restored_Then_ItIsRestoredAndTheLiveOneFlaggedCompromised()
    {
        // Arrange: another channel of this database (a new open with a stale key file) holds key index 2; a closed one
        // holds key index 3
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false), (2, false), (3, false));
        var live = _node.AddChannel(2, channelId: new ChannelId(Enumerable.Repeat((byte)0xE2, 32).ToArray()));
        var closed = _node.AddChannel(3, state: ChannelState.Closed,
                                      channelId: new ChannelId(Enumerable.Repeat((byte)0xE3, 32).ToArray()));
        _storedChannels.AddRange([live, closed]);
        var service = CreateService();

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert: all three restored (a recovery channel signs nothing); 2 carries the warning about the live channel,
        // which advises a cooperative close and forbids a force close; the Closed row at 3 is no conflict
        Assert.All(result.Channels, c => Assert.Equal(ChannelRestoreAction.Restore, c.Action));
        Assert.Equal(3, result.RestoredCount);
        var shared = result.Channels[1].Detail;
        Assert.Contains("KEYS SHARED", shared);
        Assert.Contains("key index 2", shared);
        Assert.Contains(live.ChannelId.ToString(), shared);
        Assert.Contains("cooperatively", shared);
        Assert.Contains("never force close", shared);
        Assert.DoesNotContain("KEYS SHARED", result.Channels[0].Detail);
        Assert.DoesNotContain("KEYS SHARED", result.Channels[2].Detail);
        Assert.Contains(_registered, c => c.ChannelId == result.Channels[1].Entry.ChannelId);
    }

    [Fact]
    public async Task Given_AnIndexThisProcessHandedOut_When_Restored_Then_ItIsRestoredWithTheWarning()
    {
        // Arrange: an open of this process took key index 2 and stored nothing yet
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false), (2, false));
        var gate = new ChannelKeyIndexGate();
        Assert.Equal(2u, gate.Allocate(() => 2));
        var service = CreateService(keyIndexGate: gate);

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.All(result.Channels, c => Assert.Equal(ChannelRestoreAction.Restore, c.Action));
        Assert.DoesNotContain("KEYS SHARED", result.Channels[0].Detail);
        Assert.Contains("handed out to an open not stored yet", result.Channels[1].Detail);
    }

    [Fact]
    public async Task Given_AnUnconsumedRemoteAllocation_When_Restored_Then_ItsIndexCountsAsInUse()
    {
        // Arrange: the native remote signer's saved allocation of key index 2 (a restart replays it when the peer
        // sends its open again), no channel row and nothing handed out in this process
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false), (2, false));
        var pending = new Mock<IPendingChannelKeyIndexSource>();
        pending.Setup(p => p.GetPendingKeyIndexesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([2u]);
        var service = CreateService(pendingKeyIndexSources: [pending.Object]);

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Contains("KEYS SHARED: key index 2", result.Channels[1].Detail);
        Assert.DoesNotContain("KEYS SHARED", result.Channels[0].Detail);
    }

    [Fact]
    public async Task Given_TwoBackedUpChannelsAtOneIndex_When_Restored_Then_BothCarryTheWarning()
    {
        // Arrange: an inconsistent backup
        var ct = TestContext.Current.CancellationToken;
        var source = new BackupTestData();
        source.AddChannel(2);
        source.AddChannel(2, channelId: new ChannelId(Enumerable.Repeat((byte)0xD2, 32).ToArray()));
        var backup = (await source.CreateService().ExportAsync(null, ct)).Backup;
        var service = CreateService();

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert
        Assert.Equal(2, result.Channels.Count);
        Assert.All(result.Channels, c => Assert.Contains("also used by backed-up channel", c.Detail));
    }

    [Fact]
    public async Task Given_ARestoreInProgress_When_AnOpenAsksForAKeyIndex_Then_ItIsRefusedUntilTheRestoreEnds()
    {
        // Arrange: the "open" asks while the restore raises the key indexes
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var gate = new ChannelKeyIndexGate();
        ChannelKeyIndexHeldException? refusal = null;
        string? reasonDuringRestore = null;
        var reserver = new Mock<IChannelKeyIndexReserver>();
        reserver.Setup(r => r.ReserveThroughAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                .Callback(() =>
                 {
                     reasonDuringRestore = gate.HoldReason;
                     refusal = Assert.Throws<ChannelKeyIndexHeldException>(() => gate.Allocate(() => 9));
                 })
                .ReturnsAsync(1u);
        var service = CreateService(keyIndexReserver: reserver.Object, keyIndexGate: gate);
        var reasonWhileDialing = "not dialed";
        _peerManager.Setup(p => p.ConnectToPeerAsync(It.IsAny<PeerAddressInfo>()))
                    .Callback(() => reasonWhileDialing = gate.HoldReason)
                    .ReturnsAsync((PeerAddressInfo _) => new PeerModel(BackupTestData.Key(0x03, 1, 9), "h", 1,
                                                                       "IPv4"));

        // Act
        await service.RestoreAsync(backup, ct);

        // Assert: refused while the key indexes are raised with a retryable reason; the hold is gone before the
        // peers are dialed, so a peer's open meanwhile is not refused; allowed after it
        Assert.Null(reasonWhileDialing);
        Assert.Equal(ChannelKeyIndexHold.RestoreInProgressReason, reasonDuringRestore);
        Assert.NotNull(refusal);
        Assert.Contains("try again later", refusal.Message);
        Assert.False(gate.IsAllocatedByThisProcess(9));
        Assert.Null(gate.HoldReason);
        Assert.Equal(10u, gate.Allocate(() => 10));
    }

    [Fact]
    public async Task Given_TheStartHold_When_ARestoreRaisesTheKeyIndexes_Then_OpensAreAllowedAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var gate = new ChannelKeyIndexGate();
        gate.HoldUntilRestore();
        var service = CreateService(keyIndexReserver: Reserver(), keyIndexGate: gate);
        Assert.Equal(ChannelKeyIndexHold.AwaitingRestoreReason, gate.HoldReason);

        // Act
        await service.RestoreAsync(backup, ct);

        // Assert
        Assert.False(gate.IsAwaitingRestore);
        Assert.Null(gate.HoldReason);
    }

    [Fact]
    public async Task Given_TheStartHold_When_TheRestoreRaisesNothing_Then_TheHoldStays()
    {
        // Arrange: a backup whose only channel is already stored, and one restore without a reserver
        var ct = TestContext.Current.CancellationToken;
        var gate = new ChannelKeyIndexGate();
        gate.HoldUntilRestore();
        var existing = await ExportAsync(ct, (1, false));
        _storedChannels.Add(new BackupTestData().AddChannel(1));
        var withReserver = CreateService(keyIndexReserver: Reserver(), keyIndexGate: gate);
        var withoutReserver = CreateService(keyIndexGate: gate);
        var fresh = await ExportAsync(ct, (2, false));

        // Act
        await withReserver.RestoreAsync(existing, ct);
        var afterNothingToRestore = gate.IsAwaitingRestore;
        await withoutReserver.RestoreAsync(fresh, ct);

        // Assert
        Assert.True(afterNothingToRestore);
        Assert.True(gate.IsAwaitingRestore);
    }

    private static IChannelKeyIndexReserver Reserver()
    {
        var reserver = new Mock<IChannelKeyIndexReserver>();
        reserver.Setup(r => r.ReserveThroughAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((uint index, CancellationToken _) => index);
        return reserver.Object;
    }

    [Fact]
    public async Task Given_InboundOpensRacingARestore_When_BothRun_Then_NoRestoredChannelSharesAKeyIndexWithAnOpen()
    {
        // Arrange: a node set up on a fresh database with a stale key file (last used index 2) while the backup's
        // channels use key indexes 1 to 6. Every "inbound open" gets its key index from the production local signer
        // (the factory's call), claimed in the database's high-water mark; the restore raises the same key manager
        // and mark through the production reserver
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false), (2, false), (3, false), (4, true), (5, false), (6, false));
        for (var round = 0; round < 10; round++)
        {
            ResetStore();
            var gate = new ChannelKeyIndexGate();
            var keyManager = new CountingKeyManager(2);
            var store = new InMemoryKeyIndexStore(2);
            var signer = new LocalLightningSigner(new FundingOutputBuilder(), new KeyDerivationService(),
                                                  NullLogger<LocalLightningSigner>.Instance, new NodeOptions(),
                                                  keyManager.Object, Mock.Of<IUtxoMemoryRepository>(),
                                                  keyIndexStore: store, keyIndexGate: gate);
            var reserver = new SecureKeyManagerKeyIndexReserver(keyManager.Object, keyIndexStore: store);
            var service = CreateService(keyIndexReserver: reserver, keyIndexGate: gate);

            // Act: 8 opens and the restore start together
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var opens = Enumerable.Range(0, 8).Select(open => Task.Run(async () =>
            {
                await go.Task;
                try
                {
                    return (uint?)signer.CreateNewChannel(out _, out _);
                }
                catch (ChannelKeyIndexHeldException)
                {
                    return null;
                }
            }, ct)).ToList();
            var restore = Task.Run(async () =>
            {
                await go.Task;
                return await service.RestoreAsync(backup, ct);
            }, ct);
            go.SetResult();
            var handedOut = (await Task.WhenAll(opens)).OfType<uint>().ToList();
            var result = await restore;

            // Assert: the opens' indexes are distinct; a backed-up channel at an index an open took is never
            // restored silently: it carries the shared-keys warning, and only those do
            Assert.Equal(handedOut.Count, handedOut.Distinct().Count());
            Assert.All(result.Channels, c => Assert.Equal(ChannelRestoreAction.Restore, c.Action));
            foreach (var channel in result.Channels)
                Assert.Equal(handedOut.Contains(channel.Entry.KeyIndex), channel.Detail.Contains("KEYS SHARED"));

            // An open after the restore gets an index past every backed-up one
            var later = signer.CreateNewChannel(out _, out _);
            Assert.True(later > 6, $"index {later} after the restore");
        }
    }

    /// <summary>Forgets what the previous round of a test stored.</summary>
    private void ResetStore()
    {
        _storedChannels.Clear();
        _storedPeers.Clear();
        _storedWatches.Clear();
        _registered.Clear();
        _tracked.Clear();
        lock (_connects)
            _connects.Clear();
        _calls.Clear();
    }

    /// <summary>A key manager whose channel index counts up from a last used one, safe for concurrent callers.</summary>
    private sealed class CountingKeyManager
    {
        private readonly Lock _lock = new();
        private readonly Mock<ISecureKeyManager> _mock = new();
        private uint _lastUsed;

        public CountingKeyManager(uint lastUsed)
        {
            _lastUsed = lastUsed;
            _mock.Setup(k => k.ReserveChannelKeyIndex()).Returns(() =>
            {
                lock (_lock)
                    return ++_lastUsed;
            });
            _mock.Setup(k => k.EnsureLastUsedChannelIndexAtLeast(It.IsAny<uint>())).Returns((uint highest) =>
            {
                lock (_lock)
                {
                    if (highest <= _lastUsed)
                        return false;
                    _lastUsed = highest;
                    return true;
                }
            });
            _mock.Setup(k => k.GetChannelKeyAtIndex(It.IsAny<uint>()))
                 .Returns((uint index) => s_channelRoot.Derive(index).ToBytes());
        }

        public ISecureKeyManager Object => _mock.Object;
    }

    /// <summary>The database's high-water mark (NL-1388) in memory: one atomic claim at a time.</summary>
    private sealed class InMemoryKeyIndexStore(uint highWater) : IChannelKeyIndexStore
    {
        private readonly Lock _lock = new();
        private uint _highWater = highWater;

        public uint? GetHighWater()
        {
            lock (_lock)
                return _highWater;
        }

        public bool TryClaim(uint index)
        {
            lock (_lock)
            {
                if (_highWater >= index)
                    return false;
                _highWater = index;
                return true;
            }
        }

        public void Raise(uint index)
        {
            lock (_lock)
                _highWater = Math.Max(_highWater, index);
        }
    }
}