using System.Buffers.Binary;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Infrastructure.Crypto.Hashes;

/// <summary>
/// Static channel backups of spliced and dual-funded channels (lane SP2-E, NL-478): the record holds the current
/// funding with our key index and the pending splices (trailing fields of codec version 1), the backup is made from the
/// stored fundings, the restore re-derives the rotated key, and a reader that predates the fields refuses the channel.
/// </summary>
public class SplicedChannelBackupTests
{
    private const byte Tag = 4;

    [Fact]
    public void Given_ASplicedEntryWithPendingFundings_When_EncodedAndDecoded_Then_TheKeyIndexAndPendingFundingsRoundTrip()
    {
        // Arrange
        var snapshot = BackupTestData.SampleSnapshot();
        var spliced = snapshot.Channels[0] with
        {
            LocalFundingKeyIndex = 7,
            PendingFundings =
            [
                new ChannelBackupFunding(new TxId(Enumerable.Repeat((byte)0x11, 32).ToArray()), 2, 1_500_000, 8,
                                         BackupTestData.Key(0x02, 1, 40), BackupTestData.Key(0x03, 1, 41)),
                new ChannelBackupFunding(new TxId(Enumerable.Repeat((byte)0x12, 32).ToArray()), 0, 1_490_000, 8,
                                         BackupTestData.Key(0x02, 1, 40), BackupTestData.Key(0x03, 1, 42))
            ]
        };
        snapshot = snapshot with { Channels = [spliced, snapshot.Channels[1]] };

        // Act
        var decoded = ChannelBackupCodec.Decode(ChannelBackupCodec.Encode(snapshot));

        // Assert
        BackupTestData.AssertSameEntry(spliced, decoded.Channels[0]);
        Assert.Equal(7u, decoded.Channels[0].LocalFundingKeyIndex);
        Assert.Equal(spliced.PendingFundings, decoded.Channels[0].PendingFundings);
        Assert.Equal(0u, decoded.Channels[1].LocalFundingKeyIndex);
        Assert.Empty(decoded.Channels[1].PendingFundings);
    }

    [Fact]
    public void Given_ARecordWrittenBeforeTheSplicingFields_When_Decoded_Then_KeyIndexZeroAndNoPendingFunding()
    {
        // Arrange: a record of the first revision ends with the addresses (5 bytes shorter: u32 index, u8 count)
        var snapshot = BackupTestData.SampleSnapshot() with
        {
            Channels = [BackupTestData.SampleSnapshot().Channels[0]]
        };
        var old = WithoutSplicingFields(ChannelBackupCodec.Encode(snapshot));

        // Act
        var decoded = ChannelBackupCodec.Decode(old);

        // Assert
        var entry = Assert.Single(decoded.Channels);
        BackupTestData.AssertSameEntry(snapshot.Channels[0], entry);
        Assert.Equal(0u, entry.LocalFundingKeyIndex);
        Assert.Empty(entry.PendingFundings);
    }

    [Fact]
    public void Given_ASplicedRecordReadWithoutItsTrailingFields_When_Planned_Then_KeysMismatchNotARestoreWithTheWrongKey()
    {
        // Arrange: what a reader that predates the fields sees of a spliced channel: the rotated funding key with key
        // index 0, which derives the original key
        var kit = new SpliceBackupKit();
        var postSplice = kit.PostSpliceEntry();
        var seenByAnOlderReader = postSplice with { LocalFundingKeyIndex = 0, PendingFundings = [] };

        // Act
        var plan = ChannelRestorePlanner.Plan([seenByAnOlderReader], _ => (false, null),
                                              kit.Signer.GetChannelBasepoints, kit.KeySource.GetFundingPubKey);
        var olderPlanner = ChannelRestorePlanner.Plan([postSplice], _ => (false, null),
                                                      kit.Signer.GetChannelBasepoints);

        // Assert: refused, never restored with the original key on the splice's outpoint
        Assert.Equal(ChannelRestoreAction.KeysMismatch, Assert.Single(plan).Action);
        Assert.Equal(ChannelRestoreAction.KeysMismatch, Assert.Single(olderPlanner).Action);
    }

    [Fact]
    public void Given_APostSpliceEntry_When_Planned_Then_RestoredWithTheRotatedKeyDerivedFromTheKeyIndex()
    {
        // Arrange
        var kit = new SpliceBackupKit();
        var entry = kit.PostSpliceEntry();

        // Act
        var item = Assert.Single(ChannelRestorePlanner.Plan([entry], _ => (false, null),
                                                            kit.Signer.GetChannelBasepoints,
                                                            kit.KeySource.GetFundingPubKey));

        // Assert
        Assert.Equal(ChannelRestoreAction.Restore, item.Action);
        Assert.Equal(kit.LocalFundingKey(1), item.LocalFundingPubKey);
        Assert.NotEqual(kit.Basepoints.FundingPubKey, item.LocalFundingPubKey);
    }

    [Fact]
    public void Given_APendingSpliceWhoseKeyDoesNotDerive_When_Planned_Then_ItIsLeftOutOfTheEntry()
    {
        // Arrange
        var kit = new SpliceBackupKit();
        var entry = kit.PreSpliceEntry(withPendingSplice: true);
        entry = entry with
        {
            PendingFundings =
            [
                entry.PendingFundings[0],
                entry.PendingFundings[0] with { FundingTxId = new TxId(new byte[32]), LocalFundingKeyIndex = 2 }
            ]
        };

        // Act
        var item = Assert.Single(ChannelRestorePlanner.Plan([entry], _ => (false, null),
                                                            kit.Signer.GetChannelBasepoints,
                                                            kit.KeySource.GetFundingPubKey));

        // Assert: restored, with only the pending splice whose key index derives its key
        Assert.Equal(ChannelRestoreAction.Restore, item.Action);
        Assert.Equal([kit.SpliceTxId], item.Entry.PendingFundings.Select(f => f.FundingTxId));
    }

    [Fact]
    public void Given_APostSpliceEntry_When_TheRecoveryChannelIsCreated_Then_ItsFundingOutputIsTheSplicesWithTheRotatedKey()
    {
        // Arrange
        var kit = new SpliceBackupKit();
        var entry = kit.PostSpliceEntry();
        using var sha256 = new Sha256();

        // Act
        var channel = RecoveryChannels.Create(entry, kit.Basepoints, kit.LocalFundingKey(1), sha256);
        var (current, pending) = RecoveryChannels.CreateFundings(entry);

        // Assert: the funding output is the splice's; the key sets keep the basepoints
        Assert.True(RecoveryChannels.IsRecoveryChannel(channel));
        Assert.Equal(kit.SpliceTxId, channel.FundingOutput!.TransactionId);
        Assert.Equal(SpliceBackupKit.SpliceOutputIndex, channel.FundingOutput.Index);
        Assert.Equal(LightningMoney.Satoshis(SpliceBackupKit.SpliceSat), channel.FundingOutput.Amount);
        Assert.Equal(kit.LocalFundingKey(1), channel.LocalFundingPubKey);
        Assert.Equal(kit.RemoteFundingKey, channel.RemoteFundingPubKey);
        Assert.Equal(kit.Basepoints.FundingPubKey, channel.LocalKeySet.FundingCompactPubKey);
        Assert.Equal(kit.Basepoints.PaymentBasepoint, channel.LocalKeySet.PaymentCompactBasepoint);
        Assert.Equal(entry.ShortChannelId, channel.ShortChannelId);
        Assert.True(RecoveryChannels.HasSpliceFundings(entry));
        Assert.Equal(ChannelFundingKind.Splice, current.Kind);
        Assert.Equal(ChannelFundingStatus.Current, current.Status);
        Assert.Equal(1u, current.LocalFundingKeyIndex);
        Assert.Equal(kit.SpliceTxId, current.FundingTxId);
        Assert.Empty(pending);

        // The overload without the derived key refuses a rotated key, and a wrong derived key is refused
        Assert.Throws<ArgumentException>(() => RecoveryChannels.Create(entry, kit.Basepoints, sha256));
        Assert.Throws<ArgumentException>(() => RecoveryChannels.Create(entry, kit.Basepoints, kit.LocalFundingKey(2),
                                                                       sha256));
    }

    [Fact]
    public void Given_APreSpliceEntryWithAPendingSplice_When_FundingsAreCreated_Then_TheSpliceIsAPendingRowOnThePeersSide()
    {
        // Arrange
        var kit = new SpliceBackupKit();
        var entry = kit.PreSpliceEntry(withPendingSplice: true);

        // Act
        var (current, pending) = RecoveryChannels.CreateFundings(entry);

        // Assert: the initial funding stays Initial (key index 0); the splice pending, conserving value
        Assert.Equal(ChannelFundingKind.Initial, current.Kind);
        Assert.Equal(kit.FundingTxId, current.FundingTxId);
        var splice = Assert.Single(pending);
        Assert.Equal(ChannelFundingStatus.Pending, splice.Status);
        Assert.Equal(ChannelFundingKind.Splice, splice.Kind);
        Assert.Equal(1u, splice.LocalFundingKeyIndex);
        Assert.Equal((SpliceBackupKit.SpliceSat - SpliceBackupKit.FundingSat) * 1_000,
                     splice.LocalBalanceDeltaMsat + splice.RemoteBalanceDeltaMsat);
        Assert.Equal(0, splice.LocalBalanceDeltaMsat);
    }

    [Fact]
    public async Task Given_ASplicedChannel_When_BackedUp_Then_TheEntryHoldsTheCurrentFundingItsKeyIndexAndThePendingSplice()
    {
        // Arrange: a channel whose splice locked (funding key index 2) with another splice pending
        var ct = TestContext.Current.CancellationToken;
        var data = new BackupTestData();
        var channel = data.AddChannel(Tag, anchors: true, scid: new ShortChannelId(800, 1, 0));
        var spliceTxId = new TxId(Enumerable.Repeat((byte)0xC1, 32).ToArray());
        var rotated = FundingKey(Tag, 2);
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_200_000), rotated,
                                                           BackupTestData.Key(0x03, Tag, 30), spliceTxId, 1));
        channel.ShortChannelId = new ShortChannelId(900, 3, 1);
        var current = new ChannelFunding(spliceTxId, 1, 1_200_000, rotated, BackupTestData.Key(0x03, Tag, 30), 2, 0, 0,
                                         ChannelFundingKind.Splice, ChannelFundingStatus.Current);
        var pending = new ChannelFunding(new TxId(Enumerable.Repeat((byte)0xC2, 32).ToArray()), 0, 1_150_000,
                                         FundingKey(Tag, 3), BackupTestData.Key(0x03, Tag, 31), 3, -50_000_000, 0,
                                         ChannelFundingKind.Splice, ChannelFundingStatus.Pending);
        data.FundingSets[channel.ChannelId] = new FundingSet(current, [pending]);
        var keys = new FakeFundingKeySource(data);
        var service = data.CreateService(fundingKeySource: keys);

        // Act
        var export = await service.ExportAsync(null, ct);
        var verification = await service.VerifyAsync(export.Backup, ct);
        var decoded = service.Decrypt(export.Backup);

        // Assert
        var entry = Assert.Single(decoded.Channels);
        Assert.Equal(spliceTxId, entry.FundingTxId);
        Assert.Equal((ushort)1, entry.FundingOutputIndex);
        Assert.Equal(1_200_000ul, entry.CapacitySat);
        Assert.Equal(new ShortChannelId(900, 3, 1), entry.ShortChannelId);
        Assert.Equal(2u, entry.LocalFundingKeyIndex);
        Assert.Equal(rotated, entry.LocalFundingPubKey);
        Assert.Equal(BackupTestData.Key(0x03, Tag, 30), entry.RemoteFundingPubKey);
        var backedUpPending = Assert.Single(entry.PendingFundings);
        Assert.Equal(pending.FundingTxId, backedUpPending.FundingTxId);
        Assert.Equal(3u, backedUpPending.LocalFundingKeyIndex);
        Assert.Equal(1_150_000ul, backedUpPending.CapacitySat);
        Assert.True(verification.IsValid, verification.Error);
        Assert.True(Assert.Single(verification.Channels).KeysMatch);

        // A signer that derives no rotated key fails the check instead of trusting the backup
        var withoutRotation = await data.CreateService().VerifyAsync(export.Backup, ct);
        Assert.False(withoutRotation.IsValid);
    }

    [Fact]
    public async Task Given_ADualFundedChannel_When_BackedUpAndPlanned_Then_ItsVersionAndV2ChannelIdComeBack()
    {
        // Arrange: a v2 (dual-funded) channel, never spliced
        var ct = TestContext.Current.CancellationToken;
        var data = new BackupTestData();
        var channel = data.AddChannel(Tag, anchors: true, initiator: false, version: ChannelVersion.V2,
                                      scid: new ShortChannelId(801, 2, 0));
        var service = data.CreateService();

        // Act
        var decoded = service.Decrypt((await service.ExportAsync(null, ct)).Backup);
        var entry = Assert.Single(decoded.Channels);
        var item = Assert.Single(ChannelRestorePlanner.Plan([entry], _ => (false, null),
                                                            data.Signer.Object.GetChannelBasepoints));
        using var sha256 = new Sha256();
        var recovery = RecoveryChannels.Create(entry, item.LocalBasepoints!.Value, sha256);

        // Assert
        Assert.Equal(ChannelVersion.V2, entry.Version);
        Assert.Equal(channel.ChannelId, entry.ChannelId);
        Assert.Equal(0u, entry.LocalFundingKeyIndex);
        Assert.Equal(ChannelRestoreAction.Restore, item.Action);
        Assert.Equal(ChannelVersion.V2, recovery.Version);
        Assert.Equal(channel.ChannelId, recovery.ChannelId);
        Assert.Equal(channel.FundingOutput!.TransactionId, recovery.FundingOutput!.TransactionId);
        Assert.False(recovery.IsInitiator);
    }

    /// <summary>A fake rotated funding key of channel key index <paramref name="tag"/>.</summary>
    internal static CompactPubKey FundingKey(byte tag, uint fundingKeyIndex) =>
        BackupTestData.Key(0x02, tag, (byte)(20 + fundingKeyIndex));

    /// <summary>A plaintext whose records lose the splicing revision's 5 trailing bytes (no pending funding).</summary>
    private static byte[] WithoutSplicingFields(byte[] encoded)
    {
        const int header = 1 + 32 + 33 + 8;
        var count = BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(header));
        var output = new List<byte>(encoded[..(header + 2)]);
        var offset = header + 2;
        for (var i = 0; i < count; i++)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(encoded.AsSpan(offset));
            var shorter = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(shorter, (ushort)(length - 5));
            output.AddRange(shorter);
            output.AddRange(encoded.AsSpan(offset + 2, length - 5).ToArray());
            offset += 2 + length;
        }

        return output.ToArray();
    }

    /// <summary>
    /// Funding keys of <see cref="BackupTestData"/>'s fake signer: index 0 is its basepoint, index i the fake
    /// <see cref="FundingKey"/>.
    /// </summary>
    internal sealed class FakeFundingKeySource(BackupTestData data) : IChannelFundingKeySource
    {
        public CompactPubKey? GetFundingPubKey(uint channelKeyIndex, uint fundingKeyIndex) =>
            fundingKeyIndex == 0
                ? data.Signer.Object.GetChannelBasepoints(channelKeyIndex).FundingPubKey
                : FundingKey((byte)channelKeyIndex, fundingKeyIndex);
    }
}