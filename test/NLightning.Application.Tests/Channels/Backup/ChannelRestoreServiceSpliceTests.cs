using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Application.Onchain.Interfaces;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Proof of lane SP2-E (NL-478): a user restoring from a backup taken before or after a splice recovers the channel at
/// its current funding. The backup is decrypted with our node key, our rotated funding key is derived by the real
/// <c>LocalLightningSigner</c>, and the chain is read by the real <see cref="ChainFundingSpendLocator"/> over blocks that
/// hold the real splice and the peer's commitment: the recovery channel is made at the splice's funding output with our
/// key index 1, and the peer's commitment on it is handed to the on-chain watcher (which sweeps our <c>to_remote</c> with
/// the payment basepoint, the same across splices).
/// </summary>
public partial class ChannelRestoreServiceTests
{
    private const uint SpliceTip = 700;
    private const uint CommitmentHeight = 650;

    private readonly Dictionary<ChannelId, List<ChannelFunding>> _storedFundings = [];
    private readonly List<(ChannelId ChannelId, ChannelFunding Current)> _fundingLocks = [];
    private readonly HashSet<OutPoint> _unspent = [];
    private readonly Dictionary<uint, Block> _chainBlocks = [];

    [Fact]
    public async Task Given_APreSpliceBackupAndThePeersCommitmentOnTheSplice_When_Restored_Then_RestoredAtTheSpliceAndTheCommitmentHandedOver()
    {
        // Arrange: the backup was taken before the splice; the splice confirmed, then the peer force-closed on it
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit();
        MineSpliceAndCommitment(kit, withCommitment: true);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: watcher.Object,
                                    fundingKeySource: kit.KeySource);

        // Act
        var result = await service.RestoreAsync(Encrypt(kit.PreSpliceEntry()), ct);

        // Assert: followed through the splice and restored at its funding output with our rotated key
        var restored = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, restored.Action);
        Assert.StartsWith("FundingSpliced", restored.Detail);
        Assert.Contains(kit.SpliceTxId.ToString(), restored.Detail);
        Assert.Contains("already closed", restored.Detail);
        var channel = Assert.Single(_storedChannels);
        AssertAtSplice(kit, channel);

        // ...its funding row is the splice's with key index 1, and its outpoint is watched
        var current = Assert.Single(_storedFundings[channel.ChannelId]);
        Assert.Equal(ChannelFundingStatus.Current, current.Status);
        Assert.Equal(ChannelFundingKind.Splice, current.Kind);
        Assert.Equal(1u, current.LocalFundingKeyIndex);
        Assert.Equal(kit.LocalFundingKey(1), current.LocalFundingPubKey);
        var watch = Assert.Single(_storedWatches);
        Assert.Equal(kit.SpliceTxId, watch.TransactionId);
        Assert.Equal((uint)SpliceBackupKit.SpliceOutputIndex, watch.OutputIndex);

        // ...and the peer's commitment on the splice went to the on-chain watcher, recorded on that watch
        var spend = Assert.Single(handed);
        Assert.Equal(channel.ChannelId, spend.ChannelId);
        Assert.Equal(kit.SpliceTxId, spend.SpentTransactionId);
        Assert.Equal((uint)SpliceBackupKit.SpliceOutputIndex, spend.SpentOutputIndex);
        Assert.Equal(new TxId(kit.Commitment.GetHash().ToBytes()), spend.SpendingTransaction.TxId);
        Assert.Equal(CommitmentHeight, spend.BlockHeight);
        var marked = Assert.Single(_markedSpent);
        Assert.Equal(kit.SpliceTxId, marked.TxId);
    }

    [Fact]
    public async Task Given_APreSpliceBackupAndAnOpenSplice_When_Restored_Then_TheChannelWaitsForThePeerAtTheSplice()
    {
        // Arrange: the splice confirmed and its funding output is unspent (the peer closes on our reestablish)
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit(peerRotatesItsKey: false);
        MineSpliceAndCommitment(kit, withCommitment: false);
        _unspent.Add(kit.SpliceOutPoint);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: watcher.Object,
                                    fundingKeySource: kit.KeySource);

        // Act
        var result = await service.RestoreAsync(Encrypt(kit.PreSpliceEntry()), ct);

        // Assert
        var restored = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, restored.Action);
        Assert.StartsWith("FundingSpliced", restored.Detail);
        AssertAtSplice(kit, Assert.Single(_storedChannels));
        Assert.Equal(kit.SpliceTxId, Assert.Single(_tracked).TransactionId);
        Assert.Empty(handed);
        Assert.True(Assert.Single(result.Peers).Connected);
    }

    [Fact]
    public async Task Given_APostSpliceBackup_When_Restored_Then_RestoredAtTheSpliceWithItsKeyIndexWithoutFollowing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit();
        MineSpliceAndCommitment(kit, withCommitment: false);
        _unspent.Add(kit.SpliceOutPoint);
        var service = CreateService(spendLocator: CreateChainLocator(), fundingKeySource: kit.KeySource);

        // Act
        var result = await service.RestoreAsync(Encrypt(kit.PostSpliceEntry()), ct);

        // Assert
        var restored = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, restored.Action);
        Assert.DoesNotContain("FundingSpliced", restored.Detail);
        var channel = Assert.Single(_storedChannels);
        AssertAtSplice(kit, channel);
        var current = Assert.Single(_storedFundings[channel.ChannelId]);
        Assert.Equal(1u, current.LocalFundingKeyIndex);
        Assert.Equal(ChannelFundingKind.Splice, current.Kind);
    }

    [Fact]
    public async Task Given_APostSpliceBackupAndASignerWithoutRotatedKeys_When_Restored_Then_TheChannelIsRefused()
    {
        // Arrange: a funding key source that derives index 0 only
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit();
        var service = CreateService(fundingKeySource: new OnlyOriginalFundingKeys(kit));

        // Act
        var result = await service.RestoreAsync(Encrypt(kit.PostSpliceEntry()), ct);

        // Assert
        Assert.Equal(ChannelRestoreAction.KeysMismatch, Assert.Single(result.Channels).Action);
        Assert.Empty(_storedChannels);
        Assert.Empty(_connects);
    }

    [Fact]
    public async Task Given_ARecoveryChannelMadeBeforeItsSpliceConfirmed_When_RestoredAgain_Then_ItIsMovedToTheSplice()
    {
        // Arrange: the backup names the pending splice; the first restore runs while the old funding is unspent
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit(peerRotatesItsKey: true);
        _unspent.Add(kit.FundingOutPoint);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? found) =>
               {
                   found = _storedChannels.FirstOrDefault(c => c.ChannelId == id);
                   return found is not null;
               }));
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: watcher.Object,
                                    fundingKeySource: kit.KeySource, channelMemory: memory.Object);
        var backup = Encrypt(kit.PreSpliceEntry(withPendingSplice: true));
        var first = await service.RestoreAsync(backup, ct);
        var channel = Assert.Single(_storedChannels);
        Assert.Equal(kit.FundingTxId, channel.FundingOutput!.TransactionId);
        Assert.Equal([ChannelFundingStatus.Current, ChannelFundingStatus.Pending],
                     _storedFundings[channel.ChannelId].Select(f => f.Status));
        Assert.Equal(ChannelRestoreAction.Restore, Assert.Single(first.Channels).Action);

        // Act: the splice confirms, then the peer's commitment on it; the restore runs again
        _unspent.Remove(kit.FundingOutPoint);
        MineSpliceAndCommitment(kit, withCommitment: true);
        var second = await service.RestoreAsync(backup, ct);

        // Assert: the stored recovery channel moved to the splice (the peer's rotated key from the backup)
        var again = Assert.Single(second.Channels);
        Assert.Equal(ChannelRestoreAction.AlreadyExists, again.Action);
        Assert.Contains("FundingSpliced", again.Detail);
        AssertAtSplice(kit, channel, kit.SpliceRemoteKey);
        var locked = Assert.Single(_fundingLocks);
        Assert.Equal(kit.SpliceTxId, locked.Current.FundingTxId);
        Assert.Equal(1u, locked.Current.LocalFundingKeyIndex);
        Assert.Equal(kit.SpliceRemoteKey, locked.Current.RemoteFundingPubKey);
        Assert.Contains(_tracked, w => w.TransactionId == kit.SpliceTxId);
        Assert.Contains(_storedWatches, w => w.TransactionId == kit.SpliceTxId);
        var spend = Assert.Single(handed);
        Assert.Equal(kit.SpliceTxId, spend.SpentTransactionId);

        // NL-138: the memory model was published through UpdateChannel, so the backup monitor and the channel update
        // service (its channel_update follows the new short channel id) learn of the move
        memory.Verify(m => m.UpdateChannel(It.Is<ChannelModel>(c => c.ChannelId == channel.ChannelId
                                                                    && c.FundingOutput!.TransactionId == kit.SpliceTxId)),
                      Times.Once);

        // ...and a third run with the same (older) backup moves nothing again
        var third = await service.RestoreAsync(backup, ct);
        Assert.Equal(ChannelRestoreAction.AlreadyExists, Assert.Single(third.Channels).Action);
        Assert.Single(_fundingLocks);
        AssertAtSplice(kit, channel, kit.SpliceRemoteKey);
        Assert.Equal(ChannelFundingStatus.Current,
                     _storedFundings[channel.ChannelId].Single(f => f.FundingTxId == kit.SpliceTxId).Status);
    }

    [Fact]
    public async Task Given_ASpliceOlderThanTheSearchWindow_When_Restored_Then_TheBackgroundSearchMovesTheChannelAndHandsTheCommitmentOver()
    {
        // Arrange: the recent window (20 blocks) holds neither the splice (100 blocks down) nor the commitment (50)
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit();
        MineSpliceAndCommitment(kit, withCommitment: true);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? found) =>
               {
                   found = _storedChannels.FirstOrDefault(c => c.ChannelId == id);
                   return found is not null;
               }));
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(depth: 20), onchainWatcher: watcher.Object,
                                    fundingKeySource: kit.KeySource, channelMemory: memory.Object);

        // Act
        var result = await service.RestoreAsync(Encrypt(kit.PreSpliceEntry()), ct);
        await service.WaitForBackgroundWorkAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert: restored at the backed-up funding, then moved by the background search, which also found the close
        Assert.StartsWith("FundingSpendRescan", Assert.Single(result.Channels).Detail);
        var channel = Assert.Single(_storedChannels);
        AssertAtSplice(kit, channel);
        Assert.Equal(kit.SpliceTxId, Assert.Single(_fundingLocks).Current.FundingTxId);
        var spend = Assert.Single(handed);
        Assert.Equal(kit.SpliceTxId, spend.SpentTransactionId);
        Assert.Equal(new TxId(kit.Commitment.GetHash().ToBytes()), spend.SpendingTransaction.TxId);
    }

    private SpliceBackupKit UseSpliceKit(bool peerRotatesItsKey = false)
    {
        var kit = new SpliceBackupKit(peerRotatesItsKey);
        _node.Signer.Setup(s => s.GetChannelBasepoints(SpliceBackupKit.ChannelKeyIndex)).Returns(kit.Basepoints);
        return kit;
    }

    private static void AssertAtSplice(SpliceBackupKit kit, ChannelModel channel, CompactPubKey? remoteKey = null)
    {
        Assert.True(Application.Channels.Backup.RecoveryChannels.IsRecoveryChannel(channel));
        Assert.Equal(kit.SpliceTxId, channel.FundingOutput!.TransactionId);
        Assert.Equal(SpliceBackupKit.SpliceOutputIndex, channel.FundingOutput.Index);
        Assert.Equal(SpliceBackupKit.SpliceSat, channel.FundingOutput.Amount.Satoshi);
        Assert.Equal(kit.LocalFundingKey(1), channel.LocalFundingPubKey);
        Assert.Equal(remoteKey ?? kit.RemoteFundingKey, channel.RemoteFundingPubKey);
        Assert.Equal(new ShortChannelId(SpliceBackupKit.SpliceHeight, SpliceBackupKit.SpliceTransactionIndex,
                                        SpliceBackupKit.SpliceOutputIndex), channel.ShortChannelId);

        // Our payment basepoint (the key of our to_remote on the peer's commitment) is the channel's, splice or not
        Assert.Equal(kit.Basepoints.PaymentBasepoint, channel.LocalKeySet.PaymentCompactBasepoint);
    }

    /// <summary>The splice at <see cref="SpliceBackupKit.SpliceHeight"/> (third transaction of its block) and, when
    /// asked, the peer's commitment on it at <see cref="CommitmentHeight"/>.</summary>
    private void MineSpliceAndCommitment(SpliceBackupKit kit, bool withCommitment)
    {
        var filler = SpliceBackupKit.NewTransaction(0);
        filler.Inputs.Add(new TxIn(new OutPoint(uint256.One, 7)));
        filler.Outputs.Add(new TxOut(Money.Satoshis(1_000), new Key().PubKey.WitHash.ScriptPubKey));
        _chainBlocks[SpliceBackupKit.SpliceHeight] = SpliceBackupKit.BlockWith(SpliceBackupKit.SpliceHeight, filler,
                                                                               kit.Splice);
        if (withCommitment)
            _chainBlocks[CommitmentHeight] = SpliceBackupKit.BlockWith(CommitmentHeight, kit.Commitment);
    }

    private ChainFundingSpendLocator CreateChainLocator(uint depth = 4032, IMusig2Service? musig2 = null)
    {
        var chain = new Mock<IBitcoinChainService>();
        chain.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(SpliceTip);
        chain.Setup(c => c.GetBlockAsync(It.IsAny<uint>()))
             .ReturnsAsync((uint height) => _chainBlocks.TryGetValue(height, out var block)
                                                ? block
                                                : SpliceBackupKit.BlockWith(height));
        chain.Setup(c => c.GetConfirmedUnspentOutputAsync(It.IsAny<OutPoint>()))
             .ReturnsAsync((OutPoint outPoint) => _unspent.Contains(outPoint)
                                                      ? (new TxOut(Money.Satoshis(1), new Script()), 1u)
                                                      : null);
        return new ChainFundingSpendLocator(chain.Object,
                                            Options.Create(new ChannelBackupOptions { RestoreSpendSearchDepth = depth }),
                                            null, musig2);
    }

    private static (Mock<IOnchainChannelWatcher> Watcher, List<OutpointSpentEventArgs> Handed) RecordingWatcher()
    {
        var handed = new List<OutpointSpentEventArgs>();
        var watcher = new Mock<IOnchainChannelWatcher>();
        watcher.Setup(w => w.HandleFundingSpentAsync(It.IsAny<OutpointSpentEventArgs>(),
                                                     It.IsAny<CancellationToken>()))
               .Callback((OutpointSpentEventArgs args, CancellationToken _) => handed.Add(args))
               .ReturnsAsync((FundingSpendOutcome?)null);
        return (watcher, handed);
    }

    /// <summary>The backup of <paramref name="entry"/>, encrypted to our node key.</summary>
    private byte[] Encrypt(ChannelBackupEntry entry)
    {
        var snapshot = new ChannelBackupSnapshot(BitcoinNetwork.Regtest.ChainHash, _node.KeyManager.NodeId,
                                                 DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), [entry]);
        var key = ChannelBackupCipher.DeriveKey((byte[])_node.KeyManager.GetNodeKeyPair().PrivKey);
        return ChannelBackupCipher.Encrypt(key, ChannelBackupCodec.Encode(snapshot));
    }

    /// <summary>The funding repository of one unit of work: writes are staged until the save.</summary>
    private IChannelFundingDbRepository CreateFundingRepository(List<Action> staged)
    {
        var repository = new Mock<IChannelFundingDbRepository>();
        repository.Setup(r => r.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                  .ReturnsAsync((ChannelId id) => (IReadOnlyList<ChannelFunding>)Fundings(id).ToList());
        repository.Setup(r => r.GetFundingSetAsync(It.IsAny<ChannelId>()))
                  .ReturnsAsync((ChannelId id) =>
                   {
                       var rows = Fundings(id);
                       var current = rows.FirstOrDefault(f => f.Status == ChannelFundingStatus.Current);
                       return current is null
                                  ? null
                                  : new FundingSet(current,
                                                   rows.Where(f => f.Status == ChannelFundingStatus.Pending).ToList());
                   });
        repository.Setup(r => r.UpsertAsync(It.IsAny<ChannelId>(), It.IsAny<ChannelFunding>()))
                  .Callback((ChannelId id, ChannelFunding funding) => staged.Add(() => Upsert(id, funding)))
                  .Returns(Task.CompletedTask);
        repository.Setup(r => r.ApplyLockAsync(It.IsAny<ChannelId>(), It.IsAny<ChannelFunding>(),
                                               It.IsAny<IReadOnlyList<ChannelFunding>>()))
                  .Callback((ChannelId id, ChannelFunding current, IReadOnlyList<ChannelFunding> retired) =>
                                staged.Add(() =>
                                {
                                    _fundingLocks.Add((id, current));
                                    foreach (var funding in retired)
                                        Upsert(id, funding);
                                    Upsert(id, current);
                                }))
                  .Returns(Task.CompletedTask);
        return repository.Object;
    }

    private List<ChannelFunding> Fundings(ChannelId channelId)
    {
        if (!_storedFundings.TryGetValue(channelId, out var rows))
            _storedFundings[channelId] = rows = [];
        return rows;
    }

    private void Upsert(ChannelId channelId, ChannelFunding funding)
    {
        var rows = Fundings(channelId);
        var index = rows.FindIndex(f => f.FundingTxId == funding.FundingTxId);
        if (index >= 0)
            rows[index] = funding;
        else
            rows.Add(funding);
    }

    private sealed class OnlyOriginalFundingKeys(SpliceBackupKit kit)
        : Application.Channels.Backup.Interfaces.IChannelFundingKeySource
    {
        public CompactPubKey? GetFundingPubKey(uint channelKeyIndex, uint fundingKeyIndex) =>
            fundingKeyIndex == 0 ? kit.LocalFundingKey(0) : null;
    }
}