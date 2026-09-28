namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// The backup writer after the lane SP2-E review: every signed candidate of an unconfirmed dual-funded open is backed
/// up (any of them may confirm), and an entry whose funding key index does not derive its funding key is fixed by
/// derivation or never replaces a good entry of the file.
/// </summary>
public sealed class ChannelBackupReviewTests : IDisposable
{
    private const byte Tag = 6;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nltg-scb-review-{Guid.NewGuid():N}");

    public ChannelBackupReviewTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, true);
    }

    private string FilePath => Path.Combine(_directory, ChannelBackupOptions.DefaultFileName);

    [Fact]
    public async Task Given_AnUnconfirmedDualFundedOpenWithSignedRbfCandidates_When_BackedUp_Then_EachEarlierCandidateIsAPendingFunding()
    {
        // Arrange: the channel's funding is the latest RBF attempt; an earlier one was signed by both, another only
        // constructed (we never sent our tx_signatures for it)
        var ct = TestContext.Current.CancellationToken;
        var data = new BackupTestData();
        var channel = data.AddChannel(Tag, anchors: true, version: ChannelVersion.V2);
        var latest = channel.FundingOutput!.TransactionId!.Value;
        var earlier = TxIdOf(0xE1);
        var unsigned = TxIdOf(0xE2);
        data.Sessions[channel.ChannelId] =
        [
            Session(channel.ChannelId, InteractiveTxPurpose.DualFund, earlier, 1, 1_000_000 + Tag, true),
            Session(channel.ChannelId, InteractiveTxPurpose.DualFundRbf, unsigned, 0, 1_000_000 + Tag, false),
            Session(channel.ChannelId, InteractiveTxPurpose.DualFundRbf, latest, channel.FundingOutput.Index!.Value,
                    1_000_000 + Tag, true)
        ];
        var service = data.CreateService();

        // Act
        var export = await service.ExportAsync(null, ct);
        var entry = Assert.Single(service.Decrypt(export.Backup).Channels);
        var verification = await service.VerifyAsync(export.Backup, ct);

        // Assert: the earlier signed candidate, with the channel's keys, as a pending funding
        Assert.Equal(latest, entry.FundingTxId);
        var candidate = Assert.Single(entry.PendingFundings);
        Assert.Equal(earlier, candidate.FundingTxId);
        Assert.Equal((ushort)1, candidate.FundingOutputIndex);
        Assert.Equal(1_000_000ul + Tag, candidate.CapacitySat);
        Assert.Equal(0u, candidate.LocalFundingKeyIndex);
        Assert.Equal(entry.LocalFundingPubKey, candidate.LocalFundingPubKey);
        Assert.Equal(entry.RemoteFundingPubKey, candidate.RemoteFundingPubKey);
        Assert.True(verification.IsValid, verification.Error);
    }

    [Fact]
    public async Task Given_AConfirmedDualFundedChannel_When_BackedUp_Then_NoCandidateIsBackedUp()
    {
        // Arrange: the open confirmed (short channel id known): the other candidates can no longer confirm
        var ct = TestContext.Current.CancellationToken;
        var data = new BackupTestData();
        var channel = data.AddChannel(Tag, version: ChannelVersion.V2, scid: new ShortChannelId(900, 1, 0));
        data.Sessions[channel.ChannelId] =
        [
            Session(channel.ChannelId, InteractiveTxPurpose.DualFund, TxIdOf(0xE1), 0, 1_000_000, true)
        ];
        var service = data.CreateService();

        // Act
        var entry = Assert.Single(service.Decrypt((await service.ExportAsync(null, ct)).Backup).Channels);

        // Assert
        Assert.Empty(entry.PendingFundings);
    }

    [Fact]
    public async Task Given_ASplicedChannelWhoseFundingsReadFails_When_TheFileIsWritten_Then_TheKeyIndexIsFoundByDerivation()
    {
        // Arrange: spliced to funding key index 2, but the funding rows can't be read (the index would read 0)
        var ct = TestContext.Current.CancellationToken;
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        var channel = Splice(data, TxIdOf(0xC1), 2);
        data.FundingSets[channel.ChannelId] = new FundingSet(Current(channel, 2), []);
        data.FailFundingReads = true;
        var keys = new SplicedChannelBackupTests.FakeFundingKeySource(data);
        var service = data.CreateService(fundingKeySource: keys);

        // Act
        var result = await service.WriteFileAsync(ct);

        // Assert: written with the index that derives the key, which the restore accepts
        Assert.Equal(ChannelBackupWriteOutcome.Written, result.Outcome);
        Assert.Null(result.UnverifiedChannels);
        var backup = await File.ReadAllBytesAsync(FilePath, ct);
        var entry = Assert.Single(service.Decrypt(backup).Channels);
        Assert.Equal(2u, entry.LocalFundingKeyIndex);
        Assert.True((await service.VerifyAsync(backup, ct)).IsValid);
        var item = Assert.Single(ChannelRestorePlanner.Plan([entry], _ => (false, null),
                                                            data.Signer.Object.GetChannelBasepoints,
                                                            keys.GetFundingPubKey));
        Assert.Equal(ChannelRestoreAction.Restore, item.Action);
    }

    [Fact]
    public async Task Given_AChannelWhoseFundingKeyDerivesNowhere_When_TheFileIsWritten_Then_ItsPreviousEntryIsKeptAndReported()
    {
        // Arrange: a good backup of the spliced channel (and of another channel) is on disk
        var ct = TestContext.Current.CancellationToken;
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        var channel = Splice(data, TxIdOf(0xC1), 2);
        data.FundingSets[channel.ChannelId] = new FundingSet(Current(channel, 2), []);
        var other = data.AddChannel(Tag + 1);
        var keys = new SplicedChannelBackupTests.FakeFundingKeySource(data);
        var service = data.CreateService(fundingKeySource: keys);
        Assert.Equal(ChannelBackupWriteOutcome.Written, (await service.WriteFileAsync(ct)).Outcome);

        // ...then the channel's funding moves to a key no index derives, and the other channel's peer moves
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_300_000),
                                                           BackupTestData.Key(0x03, Tag, 77),
                                                           BackupTestData.Key(0x03, Tag, 30), TxIdOf(0xC3), 0));
        data.FundingSets.Remove(channel.ChannelId);
        data.Peers.RemoveAll(p => p.NodeId == other.RemoteNodeId);
        data.Peers.Add(new Domain.Node.Models.PeerModel(other.RemoteNodeId, "10.9.9.9", 9735, "IPv4"));

        // Act
        var result = await service.WriteFileAsync(ct);

        // Assert: the file is rewritten for the other channel, and keeps the good entry of the unverified one
        Assert.Equal(ChannelBackupWriteOutcome.Written, result.Outcome);
        Assert.Equal([channel.ChannelId], result.UnverifiedChannels);
        var backup = await File.ReadAllBytesAsync(FilePath, ct);
        var entries = service.Decrypt(backup).Channels;
        var kept = Assert.Single(entries, e => e.ChannelId == channel.ChannelId);
        Assert.Equal(TxIdOf(0xC1), kept.FundingTxId);
        Assert.Equal(2u, kept.LocalFundingKeyIndex);
        Assert.Equal("10.9.9.9", Assert.Single(entries, e => e.ChannelId == other.ChannelId).Addresses[0].Host);
        Assert.True((await service.VerifyAsync(backup, ct)).IsValid);
    }

    [Fact]
    public async Task Given_AChannelWhoseFundingKeyDerivesNowhereAndNoPreviousBackup_When_TheFileIsWritten_Then_ItIsLeftOut()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        var channel = data.AddChannel(Tag);
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_300_000),
                                                           BackupTestData.Key(0x03, Tag, 77),
                                                           BackupTestData.Key(0x03, Tag, 30), TxIdOf(0xC3), 0));
        var other = data.AddChannel(Tag + 1);
        var service = data.CreateService(fundingKeySource: new SplicedChannelBackupTests.FakeFundingKeySource(data));

        // Act
        var result = await service.WriteFileAsync(ct);

        // Assert
        Assert.Equal([channel.ChannelId], result.UnverifiedChannels);
        var entries = service.Decrypt(await File.ReadAllBytesAsync(FilePath, ct)).Channels;
        Assert.Equal([other.ChannelId], entries.Select(e => e.ChannelId));
    }

    private static Domain.Channels.Models.ChannelModel Splice(BackupTestData data, TxId spliceTxId,
                                                              uint fundingKeyIndex)
    {
        var channel = data.AddChannel(Tag, anchors: true, scid: new ShortChannelId(800, 1, 0));
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_200_000),
                                                           SplicedChannelBackupTests.FundingKey(Tag, fundingKeyIndex),
                                                           BackupTestData.Key(0x03, Tag, 30), spliceTxId, 1));
        channel.ShortChannelId = new ShortChannelId(900, 3, 1);
        return channel;
    }

    private static ChannelFunding Current(Domain.Channels.Models.ChannelModel channel, uint fundingKeyIndex) =>
        new(channel.FundingOutput!.TransactionId!.Value, channel.FundingOutput.Index!.Value, 1_200_000,
            channel.FundingOutput.LocalFundingPubKey, channel.FundingOutput.RemoteFundingPubKey, fundingKeyIndex, 0, 0,
            ChannelFundingKind.Splice, ChannelFundingStatus.Current);

    private static TxId TxIdOf(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    /// <summary>A stored dual-funded negotiation whose constructed transaction pays the funding output at
    /// <paramref name="index"/>.</summary>
    private static InteractiveTxSessionModel Session(ChannelId channelId, InteractiveTxPurpose purpose, TxId txId,
                                                     uint index, long capacitySat, bool signed)
    {
        var outputs = new List<InteractiveTxOutput>();
        for (var i = 0u; i <= index; i++)
            outputs.Add(new InteractiveTxOutput(i * 2, InteractiveTxParty.Local,
                                                LightningMoney.Satoshis(i == index ? capacitySat : 10_000),
                                                BitcoinScript.Empty, i == index));

        return new InteractiveTxSessionModel
        {
            ChannelId = channelId,
            SessionId = Guid.NewGuid(),
            Purpose = purpose,
            IsInitiator = true,
            FeeratePerKw = 253,
            Locktime = 0,
            Inputs = [],
            Outputs = outputs,
            LocalContribution = InteractiveTxContribution.Empty,
            ConstructedTx = new ConstructedInteractiveTx(txId, [0x02], 0, [], outputs, 1_000, index),
            CommitmentSignedSent = true,
            CommitmentSignedReceived = true,
            TxSignaturesSent = signed,
            TxSignaturesReceived = signed,
            State = signed ? InteractiveTxSessionState.Signed : InteractiveTxSessionState.AwaitingTxSignatures,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
    }
}