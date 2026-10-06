using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Node.PeerStorage;

/// <summary>
/// NL-1059: restoring a backup of a simple taproot channel taken before its splices. The splices' MuSig2 outputs are
/// spent by key path and the peer rotates its funding key at each, so no witness names a key: the restore follows the
/// chain to the peer's commitment that pays our taproot <c>to_remote</c>, or, while the last funding is still open, to
/// the funding the peer's copy of our own backup (<c>peer_storage_retrieval</c>) names.
/// </summary>
public partial class ChannelRestoreServiceTests
{
    [Fact]
    public async Task Given_TwoTaprootSplicesAndThePeersCommitment_When_Restored_Then_RestoredAtTheLastSpliceAndTheCommitmentHandedOver()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var kit = UseTaprootSpliceKit();
        MineTaprootSplices(kit, withSecondSplice: true);
        var commitment = kit.CommitmentOn(kit.Splice2FundingOutPoint);
        _chainBlocks[TaprootSpliceBackupKit.CommitmentHeight] =
            TaprootSpliceBackupKit.BlockAt(TaprootSpliceBackupKit.CommitmentHeight, commitment);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(musig2: kit.Musig2),
                                    onchainWatcher: watcher.Object, fundingKeySource: kit.KeySource);

        // Act
        var result = await service.RestoreAsync(Encrypt(kit.PreSpliceEntry()), ct);

        // Assert: both splices followed, the channel at splice 2's funding output, the commitment handed over
        var restored = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, restored.Action);
        Assert.StartsWith("FundingSpliced: followed 2 splice(s)", restored.Detail);
        var channel = Assert.Single(_storedChannels);
        Assert.True(RecoveryChannels.IsRecoveryChannel(channel));
        Assert.True(channel.ChannelParams.OptionSimpleTaproot);
        Assert.Equal(kit.Splice2TxId, channel.FundingOutput!.TransactionId);
        Assert.Equal((ushort)0, channel.FundingOutput.Index);
        Assert.Equal(TaprootSpliceBackupKit.Splice2Sat, channel.FundingOutput.Amount.Satoshi);
        Assert.Equal(kit.Basepoints.PaymentBasepoint, channel.LocalKeySet.PaymentCompactBasepoint);
        var spend = Assert.Single(handed);
        Assert.Equal(kit.Splice2TxId, spend.SpentTransactionId);
        Assert.Equal(new TxId(commitment.GetHash().ToBytes()), spend.SpendingTransaction.TxId);
    }

    [Fact]
    public async Task Given_AnOpenTaprootSpliceWithAnUnknownPeerKey_When_ThePeerHandsBackOurBackup_Then_TheChannelMovesToTheFundingItNames()
    {
        // Arrange: the splice's outputs are unspent and its peer key unknown: the restore waits
        var ct = TestContext.Current.CancellationToken;
        var kit = UseTaprootSpliceKit();
        MineTaprootSplices(kit, withSecondSplice: false);
        _unspent.Add(kit.Splice1FundingOutPoint);
        _unspent.Add(new OutPoint(kit.Splice1, 0));
        var entry = kit.PreSpliceEntry();
        var peerStorage = new Mock<IPeerStorageService>();
        peerStorage.Setup(p => p.GetRetrievals()).Returns([]);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(musig2: kit.Musig2),
                                    onchainWatcher: watcher.Object, fundingKeySource: kit.KeySource,
                                    channelMemory: StoredChannelMemory(), peerStorage: peerStorage.Object);

        var result = await service.RestoreAsync(Encrypt(entry), ct);
        var restored = Assert.Single(result.Channels);
        Assert.StartsWith("FundingSplicedUnresolved", restored.Detail);
        var channel = Assert.Single(_storedChannels);
        Assert.Equal(kit.FundingTxId, channel.FundingOutput!.TransactionId);

        // Act: on reconnection the peer hands back our blob, which names splice 1's output and our key index 1
        peerStorage.Setup(p => p.GetRetrievals()).Returns([Retrieval(entry, kit.Splice1TxId, 1, 1)]);
        await service.CheckSpliceWaitsAsync(ct);

        // Assert: moved there with our key 1 (the peer's stays the last known), watched, nothing handed over
        Assert.Equal(kit.Splice1TxId, channel.FundingOutput!.TransactionId);
        Assert.Equal((ushort)1, channel.FundingOutput.Index);
        var locked = Assert.Single(_fundingLocks).Current;
        Assert.Equal(1u, locked.LocalFundingKeyIndex);
        Assert.Equal(kit.LocalFundingKey(1), locked.LocalFundingPubKey);
        Assert.Contains(_tracked, w => w.TransactionId == kit.Splice1TxId && w.OutputIndex == 1);
        Assert.Empty(service.ChannelsWaitingForSplice);
        Assert.Empty(handed);
    }

    [Fact]
    public async Task Given_TwoTaprootSplicesTheLastOpen_When_ThePeersBlobNamesTheLast_Then_RestoredThereThroughTheFirst()
    {
        // Arrange: splice 2 spends splice 1's funding and is open; the peer already handed back our blob naming it
        var ct = TestContext.Current.CancellationToken;
        var kit = UseTaprootSpliceKit();
        MineTaprootSplices(kit, withSecondSplice: true);
        _unspent.Add(kit.Splice2FundingOutPoint);
        _unspent.Add(new OutPoint(kit.Splice2, 1));
        _unspent.Add(new OutPoint(kit.Splice1, 0));
        var entry = kit.PreSpliceEntry();
        var peerStorage = new Mock<IPeerStorageService>();
        peerStorage.Setup(p => p.GetRetrievals()).Returns([Retrieval(entry, kit.Splice2TxId, 0, 2)]);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(musig2: kit.Musig2),
                                    onchainWatcher: watcher.Object, fundingKeySource: kit.KeySource,
                                    peerStorage: peerStorage.Object);

        // Act
        var result = await service.RestoreAsync(Encrypt(entry), ct);

        // Assert: splice 1's output is the one splice 2 spends; restored at splice 2 with our key 2
        var restored = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, restored.Action);
        Assert.StartsWith("FundingSpliced: followed 2 splice(s)", restored.Detail);
        var channel = Assert.Single(_storedChannels);
        Assert.Equal(kit.Splice2TxId, channel.FundingOutput!.TransactionId);
        Assert.Equal((ushort)0, channel.FundingOutput.Index);
        Assert.Equal(kit.LocalFundingKey(2), channel.LocalFundingPubKey);
        Assert.Equal(2u, Assert.Single(_storedFundings[channel.ChannelId]).LocalFundingKeyIndex);
        Assert.Empty(handed);
    }

    [Fact]
    public async Task Given_AnOpenTaprootSpliceAndABlobOfAnotherChannel_When_Restored_Then_TheChannelKeepsWaiting()
    {
        // Arrange: the peer's retrieval names another channel only
        var ct = TestContext.Current.CancellationToken;
        var kit = UseTaprootSpliceKit();
        MineTaprootSplices(kit, withSecondSplice: false);
        _unspent.Add(kit.Splice1FundingOutPoint);
        _unspent.Add(new OutPoint(kit.Splice1, 0));
        var entry = kit.PreSpliceEntry();
        var peerStorage = new Mock<IPeerStorageService>();
        peerStorage.Setup(p => p.GetRetrievals())
                   .Returns([Retrieval(entry with { ChannelId = new ChannelId(new byte[32]) }, kit.Splice1TxId, 1, 1)]);
        var service = CreateService(spendLocator: CreateChainLocator(musig2: kit.Musig2),
                                    fundingKeySource: kit.KeySource, channelMemory: StoredChannelMemory(),
                                    peerStorage: peerStorage.Object);

        // Act
        var result = await service.RestoreAsync(Encrypt(entry), ct);

        // Assert
        Assert.StartsWith("FundingSplicedUnresolved", Assert.Single(result.Channels).Detail);
        Assert.Equal(kit.FundingTxId, Assert.Single(_storedChannels).FundingOutput!.TransactionId);
    }

    private TaprootSpliceBackupKit UseTaprootSpliceKit()
    {
        var kit = new TaprootSpliceBackupKit();
        _node.Signer.Setup(s => s.GetChannelBasepoints(TaprootSpliceBackupKit.ChannelKeyIndex))
             .Returns(kit.Basepoints);
        return kit;
    }

    private void MineTaprootSplices(TaprootSpliceBackupKit kit, bool withSecondSplice)
    {
        _chainBlocks[TaprootSpliceBackupKit.Splice1Height] =
            TaprootSpliceBackupKit.BlockAt(TaprootSpliceBackupKit.Splice1Height, kit.Splice1);
        if (withSecondSplice)
            _chainBlocks[TaprootSpliceBackupKit.Splice2Height] =
                TaprootSpliceBackupKit.BlockAt(TaprootSpliceBackupKit.Splice2Height, kit.Splice2);
    }

    /// <summary>The peer's <c>peer_storage_retrieval</c>: our blob naming the channel at a funding.</summary>
    private static PeerBackupRetrieval Retrieval(ChannelBackupEntry entry, TxId fundingTxId, ushort outputIndex,
                                                 uint keyIndex) =>
        new(entry.RemoteNodeId, DateTimeOffset.UnixEpoch, 65_531,
            new PeerBackupContents(DateTimeOffset.FromUnixTimeSeconds(1_790_000_100),
                                   [
                                       new PeerBackupChannel(entry.ChannelId, entry.RemoteNodeId, fundingTxId,
                                                             outputIndex, keyIndex, true)
                                   ]), true, []);
}