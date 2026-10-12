using Google.Protobuf;
using Grpc.Core;
using Moq;

namespace NLightning.LndGrpc.Tests;

using Application.Channels.Backup.Models;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.Constants;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;

/// <summary>LND's channel backup RPCs over the node's own static channel backups (NL-1248).</summary>
public partial class LndGrpcHostTests
{
    /// <summary>An NLightning backup's header ("NLSCB", version 1) and some ciphertext.</summary>
    private static byte[] OurBackup(byte tag) => [.. "NLSCB"u8, 1, .. Enumerable.Repeat(tag, 60)];

    private static ChannelBackupEntry BackupEntry(ChannelModel channel)
    {
        var party = ChannelBackupParty.From(new ChannelParty(LightningMoney.Satoshis(354),
                                                             LightningMoney.Satoshis(10_000),
                                                             LightningMoney.MilliSatoshis(1), 483,
                                                             LightningMoney.Satoshis(500_000), 144));
        var key = channel.RemoteNodeId;
        return new ChannelBackupEntry
        {
            ChannelId = channel.ChannelId,
            RemoteNodeId = key,
            Addresses = [],
            FundingTxId = channel.FundingOutput!.TransactionId!.Value,
            FundingOutputIndex = (ushort)channel.FundingOutput.Index!.Value,
            CapacitySat = 1_000_000,
            IsInitiator = true,
            OptionAnchorOutputs = true,
            ChannelType = [],
            KeyIndex = 0,
            LocalFundingPubKey = key,
            LocalPaymentBasepoint = key,
            RemoteFundingPubKey = key,
            RemoteRevocationBasepoint = key,
            RemotePaymentBasepoint = key,
            RemoteDelayedPaymentBasepoint = key,
            RemoteHtlcBasepoint = key,
            Local = party,
            Remote = party
        };
    }

    private ChannelBackupSnapshot Snapshot(params ChannelModel[] channels) =>
        new(ChainConstants.Regtest, CreatePubKey(1), DateTimeOffset.UtcNow, channels.Select(BackupEntry).ToList());

    [Fact]
    public async Task Given_TwoChannels_When_ExportAllChannelBackups_Then_OurMultiBackupAndOneSingleEachInLndsShapes()
    {
        // Arrange
        var first = CreateChannel(7, ChannelState.Open);
        var second = CreateChannel(8, ChannelState.Open);
        _channels.AddRange([first, second]);
        _backups.Setup(x => x.ExportAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChannelBackupExport(Snapshot(first, second), OurBackup(0xAA)));
        _backups.Setup(x => x.ExportAsync(first.ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChannelBackupExport(Snapshot(first), OurBackup(0x07)));
        _backups.Setup(x => x.ExportAsync(second.ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChannelBackupExport(Snapshot(second), OurBackup(0x08)));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var all = await connection.LightningClient.ExportAllChannelBackupsAsync(new ChanBackupExportRequest(),
                                                                                 cancellationToken: Ct);
        var one = await connection.LightningClient.ExportChannelBackupAsync(new ExportChannelBackupRequest
        {
            ChanPoint = new ChannelPoint
            {
                FundingTxidStr = first.FundingOutput!.TransactionId!.Value.ToString(),
                OutputIndex = 0
            }
        }, cancellationToken: Ct);

        // Assert
        Assert.Equal(OurBackup(0xAA), all.MultiChanBackup.MultiChanBackup_.ToByteArray());
        Assert.Equal([(byte[])first.FundingOutput.TransactionId!.Value, (byte[])second.FundingOutput!.TransactionId!.Value],
                     all.MultiChanBackup.ChanPoints.Select(p => p.FundingTxidBytes.ToByteArray()));
        Assert.Equal([OurBackup(0x07), OurBackup(0x08)],
                     all.SingleChanBackups.ChanBackups.Select(b => b.ChanBackup.ToByteArray()));
        Assert.Equal(OurBackup(0x07), one.ChanBackup.ToByteArray());
        Assert.Equal((byte[])first.FundingOutput.TransactionId!.Value, one.ChanPoint.FundingTxidBytes.ToByteArray());
    }

    [Fact]
    public async Task Given_OurBackupAndAnLndOne_When_VerifyChanBackup_Then_OursListsItsChannelsAndLndsIsRefused()
    {
        // Arrange
        var channel = CreateChannel(7, ChannelState.Open);
        var snapshot = Snapshot(channel);
        _backups.Setup(x => x.VerifyAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChannelBackupVerification(true, null, snapshot,
                                                            [new ChannelBackupEntryCheck(snapshot.Channels[0], true,
                                                                 ChannelState.Open)]));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var verified = await connection.LightningClient.VerifyChanBackupAsync(new ChanBackupSnapshot
        {
            MultiChanBackup = new MultiChanBackup { MultiChanBackup_ = ByteString.CopyFrom(OurBackup(1)) }
        }, cancellationToken: Ct);
        var lnd = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.VerifyChanBackupAsync(new ChanBackupSnapshot
            {
                MultiChanBackup = new MultiChanBackup
                {
                    MultiChanBackup_ = ByteString.CopyFrom(Enumerable.Repeat((byte)0x33, 80).ToArray())
                }
            }, cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal($"{channel.FundingOutput!.TransactionId!.Value}:0", Assert.Single(verified.ChanPoints));
        Assert.Equal(StatusCode.InvalidArgument, lnd.StatusCode);
        Assert.Contains("LND-format backups are not supported", lnd.Status.Detail);
    }

    [Fact]
    public async Task Given_TheDefaultOptions_When_RestoreChannelBackups_Then_RefusedAndNothingIsRestored()
    {
        // Arrange
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.RestoreChannelBackupsAsync(new RestoreChanBackupRequest
            {
                MultiChanBackup = ByteString.CopyFrom(OurBackup(1))
            }, cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Contains("nltg restorechanbackup", error.Status.Detail);
        _restores.Verify(x => x.RestoreAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
                         Times.Never);
    }

    [Fact]
    public async Task Given_RestoreAllowed_When_RestoreChannelBackups_Then_TheNodesRestoreRunsAndCountsTheRecoveryChannels()
    {
        // Arrange
        _serviceOptions.AllowChannelBackupRestore = true;
        var channel = CreateChannel(7, ChannelState.Open);
        var snapshot = Snapshot(channel);
        _restores.Setup(x => x.RestoreAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ChannelRestoreResult(snapshot,
                                                        [
                                                            new ChannelRestoreChannelResult(snapshot.Channels[0],
                                                                ChannelRestoreAction.Restore, "restored")
                                                        ], []));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var restored = await connection.LightningClient.RestoreChannelBackupsAsync(new RestoreChanBackupRequest
        {
            MultiChanBackup = ByteString.CopyFrom(OurBackup(1))
        }, cancellationToken: Ct);
        var foreign = await Assert.ThrowsAsync<RpcException>(
            () => connection.LightningClient.RestoreChannelBackupsAsync(new RestoreChanBackupRequest
            {
                MultiChanBackup = ByteString.CopyFrom(new byte[64])
            }, cancellationToken: Ct).ResponseAsync);

        // Assert
        Assert.Equal(1u, restored.NumRestored);
        Assert.Equal(StatusCode.InvalidArgument, foreign.StatusCode);
        _restores.Verify(x => x.RestoreAsync(It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
                         Times.Once);
    }

    [Fact]
    public async Task Given_ASubscriber_When_AChannelIsBackedUp_Then_ANewSnapshotIsSent()
    {
        // Arrange: no channel backed up at the start, then one
        var channel = CreateChannel(7, ChannelState.Open);
        var snapshots = new Queue<ChannelBackupSnapshot>([Snapshot(), Snapshot(channel)]);
        var current = Snapshot();
        _backups.Setup(x => x.CreateSnapshotAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => current = snapshots.Count > 0 ? snapshots.Dequeue() : current);
        _backups.Setup(x => x.ExportAsync(null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ChannelBackupExport(Snapshot(channel), OurBackup(0xAA)));
        _backups.Setup(x => x.ExportAsync(channel.ChannelId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChannelBackupExport(Snapshot(channel), OurBackup(0x07)));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);
        using var call = connection.LightningClient.SubscribeChannelBackups(new ChannelBackupSubscription(),
                                                                             cancellationToken: Bounded);
        await call.ResponseHeadersAsync;

        // Act
        _channelMemory.Raise(x => x.OnChannelAdded += null, _channelMemory.Object,
                             new Domain.Channels.Events.ChannelUpdatedEventArgs(channel));

        // Assert
        Assert.True(await call.ResponseStream.MoveNext(Bounded));
        Assert.Equal(OurBackup(0xAA), call.ResponseStream.Current.MultiChanBackup.MultiChanBackup_.ToByteArray());
        Assert.Single(call.ResponseStream.Current.SingleChanBackups.ChanBackups);
    }
}