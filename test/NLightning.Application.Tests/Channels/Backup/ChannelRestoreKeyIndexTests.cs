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
using Domain.Node.Options;
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
    public async Task Given_ALiveChannelAtABackedUpIndex_When_Restored_Then_OnlyThatChannelIsRefused()
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

        // Assert: 1 and 3 restored, 2 refused with what to do; nothing of channel 2 stored or dialed
        Assert.Equal([ChannelRestoreAction.Restore, ChannelRestoreAction.KeyIndexInUse, ChannelRestoreAction.Restore],
                     result.Channels.Select(c => c.Action));
        var refused = result.Channels[1];
        Assert.Contains("key index 2", refused.Detail);
        Assert.Contains(live.ChannelId.ToString(), refused.Detail);
        Assert.Contains("closechannel", refused.Detail);
        Assert.Equal(2, result.RestoredCount);
        Assert.DoesNotContain(_registered, c => c.ChannelId == refused.Entry.ChannelId);
        Assert.DoesNotContain(_storedChannels, c => c.ChannelId == refused.Entry.ChannelId);
    }

    [Fact]
    public async Task Given_AnIndexThisProcessHandedOut_When_Restored_Then_ThatChannelIsRefused()
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
        Assert.Equal(ChannelRestoreAction.Restore, result.Channels[0].Action);
        Assert.Equal(ChannelRestoreAction.KeyIndexInUse, result.Channels[1].Action);
        Assert.Contains("handed out by this process", result.Channels[1].Detail);
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

        // Act
        await service.RestoreAsync(backup, ct);

        // Assert: refused during the restore with a retryable reason, allowed after it
        Assert.Equal(ChannelKeyIndexHold.RestoreInProgressReason, reasonDuringRestore);
        Assert.NotNull(refusal);
        Assert.Contains("try again later", refusal.Message);
        Assert.False(gate.IsAllocatedByThisProcess(9));
        Assert.Null(gate.HoldReason);
        Assert.Equal(10u, gate.Allocate(() => 10));
    }

    [Fact]
    public async Task Given_TheStartHold_When_ARestoreCompletes_Then_OpensAreAllowedAgain()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var backup = await ExportAsync(ct, (1, false));
        var gate = new ChannelKeyIndexGate();
        gate.HoldUntilRestore();
        var service = CreateService(keyIndexGate: gate);
        Assert.Equal(ChannelKeyIndexHold.AwaitingRestoreReason, gate.HoldReason);

        // Act
        await service.RestoreAsync(backup, ct);

        // Assert
        Assert.False(gate.IsAwaitingRestore);
        Assert.Null(gate.HoldReason);
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

            // Assert: distinct indexes, none of them a restored channel's; every open refused got a retryable error
            Assert.Equal(handedOut.Count, handedOut.Distinct().Count());
            var restored = result.Channels.Where(c => c.Action == ChannelRestoreAction.Restore)
                                 .Select(c => c.Entry.KeyIndex).ToList();
            Assert.Empty(restored.Intersect(handedOut));
            foreach (var channel in result.Channels.Where(c => c.Action == ChannelRestoreAction.KeyIndexInUse))
                Assert.Contains(channel.Entry.KeyIndex, handedOut);
            Assert.All(result.Channels, c => Assert.True(c.Action is ChannelRestoreAction.Restore
                                                                  or ChannelRestoreAction.KeyIndexInUse));

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