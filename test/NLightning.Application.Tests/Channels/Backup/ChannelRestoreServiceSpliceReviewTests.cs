using NBitcoin;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Application.Channels.Splicing;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The restore after the lane SP2-E review (NL-478): a splice of a recovery channel that the chain monitor reports (it
/// confirmed after the restore) moves the channel instead of closing it, a spend that is no commitment and can't be
/// followed yet (the peer rotated its key and the splice's output is unspent) is never handed over as a close, and a
/// dual-funded open is restored at the candidate of its RBF that confirmed.
/// </summary>
public partial class ChannelRestoreServiceTests
{
    [Fact]
    public async Task Given_ThePeerRotatedItsKeyAndTheSpliceOutputIsUnspent_When_Restored_Then_NothingIsHandedOverAndTheChannelWaits()
    {
        // Arrange: the splice confirmed with the peer's new key; its output is unspent (the peer closes on our error)
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit(peerRotatesItsKey: true);
        MineSpliceAndCommitment(kit, withCommitment: false);
        _unspent.Add(kit.SpliceOutPoint);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: watcher.Object,
                                    fundingKeySource: kit.KeySource, channelMemory: StoredChannelMemory());

        // Act
        var result = await service.RestoreAsync(Encrypt(kit.PreSpliceEntry()), ct);

        // Assert: restored at the backed-up funding, waiting for the splice's output; the splice is no close
        var restored = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, restored.Action);
        Assert.StartsWith("FundingSplicedUnresolved", restored.Detail);
        Assert.Contains(kit.SpliceTxId.ToString(), restored.Detail);
        Assert.Empty(handed);
        var channel = Assert.Single(_storedChannels);
        Assert.True(RecoveryChannels.IsRecoveryChannel(channel));
        Assert.Equal(kit.FundingTxId, channel.FundingOutput!.TransactionId);
        Assert.Equal([channel.ChannelId], service.ChannelsWaitingForSplice);
        Assert.Contains(_markedSpent, m => m.TxId == kit.FundingTxId && m.Spender == kit.SpliceTxId);
        Assert.True(Assert.Single(result.Peers).Connected);

        // Act: a block later the peer's commitment on the splice's output is mined
        _unspent.Remove(kit.SpliceOutPoint);
        MineSpliceAndCommitment(kit, withCommitment: true);
        await service.CheckSpliceWaitsAsync(ct);

        // Assert: the witness named our rotated key: moved to the splice, and the commitment handed over
        AssertAtSplice(kit, channel, kit.SpliceRemoteKey);
        Assert.Equal(1u, Assert.Single(_fundingLocks).Current.LocalFundingKeyIndex);
        var spend = Assert.Single(handed);
        Assert.Equal(kit.SpliceTxId, spend.SpentTransactionId);
        Assert.Equal(new TxId(kit.Commitment.GetHash().ToBytes()), spend.SpendingTransaction.TxId);
        Assert.Empty(service.ChannelsWaitingForSplice);
        Assert.True(RecoveryChannels.IsRecoveryChannel(channel));
    }

    [Fact]
    public async Task Given_ASpendWhoseP2WshOutputsWereSpentWithoutOurKey_When_Restored_Then_ItIsHandedOverAsAClose()
    {
        // Arrange: the transaction that spent the funding has a P2WSH output, spent by a 2-of-2 of other keys
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit(peerRotatesItsKey: true);
        MineSpliceAndCommitment(kit, withCommitment: false);
        var stranger = SpliceBackupKit.NewTransaction(0);
        stranger.Inputs.Add(new TxIn(kit.SpliceOutPoint));
        stranger.Outputs.Add(new TxOut(Money.Satoshis(1_000_000), new Key().PubKey.WitHash.ScriptPubKey));
        var (_, strangerScript) = SpliceFundingScripts.Create(new CompactPubKey(new Key().PubKey.ToBytes()),
                                                              new CompactPubKey(new Key().PubKey.ToBytes()));
        stranger.Inputs[0].WitScript = new WitScript([[], new byte[71], new byte[71], (byte[])strangerScript]);
        _chainBlocks[CommitmentHeight] = SpliceBackupKit.BlockWith(CommitmentHeight, stranger);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: watcher.Object,
                                    fundingKeySource: kit.KeySource, channelMemory: StoredChannelMemory());

        // Act
        var result = await service.RestoreAsync(Encrypt(kit.PreSpliceEntry()), ct);

        // Assert: no splice of ours: the transaction goes to the on-chain watcher as the funding's spend
        Assert.Contains("already closed", Assert.Single(result.Channels).Detail);
        var spend = Assert.Single(handed);
        Assert.Equal(kit.SpliceTxId, spend.SpendingTransaction.TxId);
        Assert.Equal(kit.FundingTxId, spend.SpentTransactionId);
        Assert.Empty(service.ChannelsWaitingForSplice);
        Assert.Equal(kit.FundingTxId, Assert.Single(_storedChannels).FundingOutput!.TransactionId);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Given_ARecoveryChannelMadeBeforeItsSpliceConfirmed_When_TheChainMonitorReportsTheSplice_Then_TheChannelMovesInsteadOfClosing(
        bool pendingSpliceInBackup, bool peerRotatesItsKey)
    {
        // Arrange: restored while the pre-splice funding was unspent (the splice unconfirmed, or only in the mempool)
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit(peerRotatesItsKey);
        _unspent.Add(kit.FundingOutPoint);
        var (inner, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: inner.Object,
                                    fundingKeySource: kit.KeySource, channelMemory: StoredChannelMemory());
        var watcher = new SpliceFollowingOnchainChannelWatcher(() => inner.Object, () => service);
        var entry = kit.PreSpliceEntry(pendingSpliceInBackup);
        Assert.Equal(ChannelRestoreAction.Restore,
                     Assert.Single((await service.RestoreAsync(Encrypt(entry), ct)).Channels).Action);
        var channel = Assert.Single(_storedChannels);

        // Act: the splice confirms and the chain monitor hands its spend of the watched funding to the watcher
        _unspent.Remove(kit.FundingOutPoint);
        _unspent.Add(kit.SpliceOutPoint);
        MineSpliceAndCommitment(kit, withCommitment: false);
        var outcome = await watcher.HandleFundingSpentAsync(kit.SpliceSpend(entry), ct);

        // Assert: nothing recorded as a close; the channel is still a recovery channel, now at the splice (or waiting
        // at the old funding when the peer's new key is not known yet)
        Assert.Null(outcome);
        Assert.Empty(handed);
        Assert.True(RecoveryChannels.IsRecoveryChannel(channel));
        var followedAtOnce = pendingSpliceInBackup || !peerRotatesItsKey;
        if (followedAtOnce)
        {
            AssertAtSplice(kit, channel, kit.SpliceRemoteKey);
            Assert.Equal(kit.SpliceTxId, Assert.Single(_fundingLocks).Current.FundingTxId);
            Assert.Contains(_tracked, w => w.TransactionId == kit.SpliceTxId);
            Assert.Empty(service.ChannelsWaitingForSplice);
        }
        else
        {
            Assert.Equal(kit.FundingTxId, channel.FundingOutput!.TransactionId);
            Assert.Equal([channel.ChannelId], service.ChannelsWaitingForSplice);
        }

        // Act: the peer's commitment on the splice's output confirms
        _unspent.Remove(kit.SpliceOutPoint);
        MineSpliceAndCommitment(kit, withCommitment: true);
        var commitmentSpend = new OutpointSpentEventArgs(
            channel.ChannelId,
            new SignedTransaction(new TxId(kit.Commitment.GetHash().ToBytes()), kit.Commitment.ToBytes()),
            CommitmentHeight, 1, kit.SpliceTxId, SpliceBackupKit.SpliceOutputIndex, new Hash(new byte[32]));
        if (followedAtOnce)
            await watcher.HandleFundingSpentAsync(commitmentSpend, ct);
        else
            await service.CheckSpliceWaitsAsync(ct);

        // Assert: the commitment on the splice reaches the on-chain watcher, which sweeps our to_remote
        AssertAtSplice(kit, channel, kit.SpliceRemoteKey);
        var spend = Assert.Single(handed);
        Assert.Equal(kit.SpliceTxId, spend.SpentTransactionId);
        Assert.Equal(new TxId(kit.Commitment.GetHash().ToBytes()), spend.SpendingTransaction.TxId);
    }

    [Fact]
    public async Task Given_AnUnresolvedSplice_When_ANewBlockIsProcessed_Then_TheBackgroundRoundFollowsIt()
    {
        // Arrange: waiting for the rotated-key splice's output
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit(peerRotatesItsKey: true);
        MineSpliceAndCommitment(kit, withCommitment: false);
        _unspent.Add(kit.SpliceOutPoint);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: watcher.Object,
                                    fundingKeySource: kit.KeySource, channelMemory: StoredChannelMemory(),
                                    asChainMonitor: true);
        await service.RestoreAsync(Encrypt(kit.PreSpliceEntry()), ct);
        Assert.Single(service.ChannelsWaitingForSplice);

        // Act: the commitment is mined, then the chain monitor reports a block
        _unspent.Remove(kit.SpliceOutPoint);
        MineSpliceAndCommitment(kit, withCommitment: true);
        _chainMonitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(SpliceTip + 1, new Hash(new byte[32])));
        await service.WaitForBackgroundWorkAsync().WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Assert
        AssertAtSplice(kit, Assert.Single(_storedChannels), kit.SpliceRemoteKey);
        Assert.Single(handed);
        Assert.Empty(service.ChannelsWaitingForSplice);
    }

    [Fact]
    public async Task Given_ACommitmentOfARecoveryChannel_When_TheChainMonitorReportsIt_Then_ItGoesStraightToTheWatcher()
    {
        // Arrange: a post-splice recovery channel whose peer force-closed on the splice
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit();
        _unspent.Add(kit.SpliceOutPoint);
        MineSpliceAndCommitment(kit, withCommitment: false);
        var (inner, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: inner.Object,
                                    fundingKeySource: kit.KeySource, channelMemory: StoredChannelMemory());
        var watcher = new SpliceFollowingOnchainChannelWatcher(() => inner.Object, () => service);
        await service.RestoreAsync(Encrypt(kit.PostSpliceEntry()), ct);
        var channel = Assert.Single(_storedChannels);

        // Act
        await watcher.HandleFundingSpentAsync(new OutpointSpentEventArgs(
                                                  channel.ChannelId,
                                                  new SignedTransaction(new TxId(kit.Commitment.GetHash().ToBytes()),
                                                                        kit.Commitment.ToBytes()), CommitmentHeight,
                                                  1, kit.SpliceTxId, SpliceBackupKit.SpliceOutputIndex,
                                                  new Hash(new byte[32])), ct);

        // Assert
        Assert.Single(handed);
        Assert.Empty(_fundingLocks);
    }

    [Fact]
    public async Task Given_ADualFundedOpenWhoseEarlierRbfCandidateConfirmed_When_Restored_Then_RestoredAtThatCandidate()
    {
        // Arrange: backed up before the open confirmed, at its latest RBF attempt; the earlier candidate confirmed
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit();
        _unspent.Add(kit.FundingOutPoint);
        var (watcher, handed) = RecordingWatcher();
        var service = CreateService(spendLocator: CreateChainLocator(), onchainWatcher: watcher.Object,
                                    fundingKeySource: kit.KeySource);

        // Act
        var result = await service.RestoreAsync(Encrypt(UnconfirmedDualFundEntry(kit)), ct);

        // Assert
        var restored = Assert.Single(result.Channels);
        Assert.Equal(ChannelRestoreAction.Restore, restored.Action);
        Assert.StartsWith("FundingRbf", restored.Detail);
        var channel = Assert.Single(_storedChannels);
        Assert.Equal(kit.FundingTxId, channel.FundingOutput!.TransactionId);
        Assert.Equal((ushort)0, channel.FundingOutput.Index);
        Assert.Equal(kit.FundingTxId, Assert.Single(_storedWatches).TransactionId);
        Assert.False(_storedFundings.TryGetValue(channel.ChannelId, out var rows) && rows.Count > 0);
        Assert.Empty(handed);
    }

    [Fact]
    public async Task Given_ADualFundedOpenNotConfirmedAtTheRestore_When_AnEarlierCandidateConfirmsAndTheRestoreRunsAgain_Then_TheChannelMovesToIt()
    {
        // Arrange: nothing confirmed at the first restore: the channel is made at the backed-up attempt and the
        // earlier candidate kept as a pending row
        var ct = TestContext.Current.CancellationToken;
        var kit = UseSpliceKit();
        var service = CreateService(spendLocator: CreateChainLocator(), fundingKeySource: kit.KeySource,
                                    channelMemory: StoredChannelMemory());
        var backup = Encrypt(UnconfirmedDualFundEntry(kit));
        await service.RestoreAsync(backup, ct);
        var channel = Assert.Single(_storedChannels);
        Assert.Equal(s_latestAttempt, channel.FundingOutput!.TransactionId);
        Assert.Contains(_storedFundings[channel.ChannelId],
                        f => f.FundingTxId == kit.FundingTxId && f.Status == ChannelFundingStatus.Pending);

        // Act: the earlier candidate confirms; the restore runs again
        _unspent.Add(kit.FundingOutPoint);
        var again = Assert.Single((await service.RestoreAsync(backup, ct)).Channels);

        // Assert
        Assert.Equal(ChannelRestoreAction.AlreadyExists, again.Action);
        Assert.Contains("FundingRbf", again.Detail);
        Assert.Equal(kit.FundingTxId, channel.FundingOutput!.TransactionId);
        var locked = Assert.Single(_fundingLocks);
        Assert.Equal(ChannelFundingKind.Initial, locked.Current.Kind);
        Assert.Contains(_tracked, w => w.TransactionId == kit.FundingTxId);
    }

    private static readonly TxId s_latestAttempt = new(Enumerable.Repeat((byte)0x77, 32).ToArray());

    /// <summary>
    /// The backup of a dual-funded open written before it confirmed: at its latest RBF attempt (never mined here),
    /// with the kit's funding transaction as an earlier signed candidate.
    /// </summary>
    private static ChannelBackupEntry UnconfirmedDualFundEntry(SpliceBackupKit kit) =>
        kit.PreSpliceEntry() with
        {
            FundingTxId = s_latestAttempt,
            FundingOutputIndex = 1,
            ShortChannelId = null,
            FundingHeight = SpliceBackupKit.FundingHeight,
            PendingFundings =
            [
                new ChannelBackupFunding(kit.FundingTxId, 0, SpliceBackupKit.FundingSat, 0, kit.LocalFundingKey(0),
                                         kit.RemoteFundingKey)
            ]
        };

    /// <summary>Channel memory over the stored channels (the models the restore moves).</summary>
    private IChannelMemoryRepository StoredChannelMemory()
    {
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId id, out ChannelModel? found) =>
               {
                   found = _storedChannels.FirstOrDefault(c => c.ChannelId == id);
                   return found is not null;
               }));
        return memory.Object;
    }
}