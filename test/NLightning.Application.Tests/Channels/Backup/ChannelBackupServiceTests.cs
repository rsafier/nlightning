namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Protocol.ValueObjects;

public sealed class ChannelBackupServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nltg-scb-{Guid.NewGuid():N}");

    public ChannelBackupServiceTests()
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
    public async Task Given_ChannelsInEveryState_When_Snapshot_Then_OnlyFundedNotClosedChannelsAreBackedUp()
    {
        // Arrange
        var data = new BackupTestData();
        data.AddChannel(1, anchors: true, scid: new ShortChannelId(500, 1, 0));
        data.AddChannel(2, state: ChannelState.V1FundingSigned);
        data.AddChannel(3, state: ChannelState.OnchainResolving);
        data.AddChannel(4, state: ChannelState.Closed);
        data.AddChannel(5, state: ChannelState.Stale);
        data.AddChannel(6, state: ChannelState.V1Opening, withFunding: false);
        var service = data.CreateService();

        // Act
        var snapshot = await service.CreateSnapshotAsync(null, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal([1, 2, 3], snapshot.Channels.Select(c => ((byte[])c.ChannelId)[0]).ToArray());
        Assert.Equal(BitcoinNetwork.Regtest.ChainHash, snapshot.ChainHash);
        Assert.Equal(data.KeyManager.NodeId, snapshot.NodeId);
        Assert.Equal(data.Now, snapshot.CreatedAt);
        var anchors = snapshot.Channels[0];
        var channel = data.Channels[0];
        Assert.True(anchors.OptionAnchorOutputs);
        Assert.Equal(new ShortChannelId(500, 1, 0), anchors.ShortChannelId);
        Assert.Equal(channel.FundingOutput!.TransactionId!.Value, anchors.FundingTxId);
        Assert.Equal(channel.FundingOutput.Index!.Value, anchors.FundingOutputIndex);
        Assert.Equal((ulong)channel.FundingOutput.Amount.Satoshi, anchors.CapacitySat);
        Assert.Equal(1u, anchors.KeyIndex);
        Assert.Equal(channel.LocalKeySet.PaymentCompactBasepoint, anchors.LocalPaymentBasepoint);
        Assert.Equal(channel.RemoteKeySet!.PaymentCompactBasepoint, anchors.RemotePaymentBasepoint);
        Assert.Equal(channel.ChannelParams.ToChannelType().GetWireBytes(), anchors.ChannelType);
        Assert.Equal(144, anchors.Local.ToSelfDelay);
        Assert.Equal(720, anchors.Remote.ToSelfDelay);
        Assert.Equal(new ChannelBackupAddress("IPv4", "10.0.0.1", 9736), Assert.Single(anchors.Addresses));
        Assert.Null(snapshot.Channels[1].ShortChannelId);
        Assert.False(snapshot.Channels[1].OptionAnchorOutputs);
    }

    [Fact]
    public async Task Given_ThePeerAnnouncedAddresses_When_Exported_Then_TheConnectableOnesAreBackedUpAfterThePeerRow()
    {
        // Arrange: the peer row holds 10.0.0.1:9736; the graph repeats it and adds an IPv6, a DNS and a Tor address
        var data = new BackupTestData();
        var channel = data.AddChannel(1);
        var graph = BackupTestData.GraphWith(BackupTestData.GraphNodeWith(
                                                 channel.RemoteNodeId,
                                                 AddressDescriptor.FromHost(AddressDescriptorType.IPv4, "10.0.0.1",
                                                                            9736),
                                                 AddressDescriptor.FromHost(AddressDescriptorType.IPv6, "2001:db8::7",
                                                                            9735),
                                                 new AddressDescriptor(AddressDescriptorType.TorV3, new byte[35], 9735),
                                                 AddressDescriptor.FromDnsHostname("peer.example.com", 9737)));
        var service = data.CreateService(graphStore: graph.Object);

        // Act
        var export = await service.ExportAsync(null, TestContext.Current.CancellationToken);
        var decrypted = service.Decrypt(export.Backup);

        // Assert: no repeat, no Tor, the peer row first, and the addresses survive the encryption
        Assert.Equal([
                         new ChannelBackupAddress("IPv4", "10.0.0.1", 9736),
                         new ChannelBackupAddress("IPv6", "2001:db8::7", 9735),
                         new ChannelBackupAddress("DNS", "peer.example.com", 9737)
                     ], Assert.Single(decrypted.Channels).Addresses);
    }

    [Fact]
    public async Task Given_Channels_When_ExportedAndDecrypted_Then_TheSnapshotRoundTrips()
    {
        // Arrange
        var data = new BackupTestData();
        data.AddChannel(1, anchors: true);
        data.AddChannel(2, initiator: false, withPeer: false);
        var service = data.CreateService();

        // Act
        var export = await service.ExportAsync(null, TestContext.Current.CancellationToken);
        var decrypted = service.Decrypt(export.Backup);

        // Assert
        Assert.Equal(2, decrypted.Channels.Count);
        for (var i = 0; i < 2; i++)
            BackupTestData.AssertSameEntry(export.Snapshot.Channels[i], decrypted.Channels[i]);
        Assert.Empty(decrypted.Channels[1].Addresses);
    }

    [Fact]
    public async Task Given_OneChannel_When_Exported_Then_OnlyItIsInTheBackup()
    {
        // Arrange
        var data = new BackupTestData();
        data.AddChannel(1);
        var two = data.AddChannel(2);
        var service = data.CreateService();

        // Act
        var export = await service.ExportAsync(two.ChannelId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(two.ChannelId, Assert.Single(service.Decrypt(export.Backup).Channels).ChannelId);
    }

    [Fact]
    public async Task Given_AClosedOrUnknownChannel_When_Exported_Then_ItIsNotFound()
    {
        // Arrange
        var data = new BackupTestData();
        var closed = data.AddChannel(1, state: ChannelState.Closed);
        var service = data.CreateService();

        // Act / Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.ExportAsync(closed.ChannelId, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.ExportAsync(new ChannelId(new byte[32]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_OurBackup_When_Verified_Then_ItIsValidWithEveryChannelsKeysMatching()
    {
        // Arrange
        var data = new BackupTestData();
        data.AddChannel(1, anchors: true);
        data.AddChannel(2);
        var service = data.CreateService();
        var export = await service.ExportAsync(null, TestContext.Current.CancellationToken);

        // Act
        var verification = await service.VerifyAsync(export.Backup, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(verification.IsValid, verification.Error);
        Assert.Null(verification.Error);
        Assert.All(verification.Channels, c => Assert.True(c.KeysMatch));
        Assert.All(verification.Channels, c => Assert.Equal(ChannelState.Open, c.LocalState));
    }

    [Fact]
    public async Task Given_OurBackupAndAWipedDatabase_When_Verified_Then_ItIsValidAndNoChannelIsLocal()
    {
        // Arrange
        var data = new BackupTestData();
        data.AddChannel(1);
        var provider = data.BuildProvider();
        var service = data.CreateService(provider);
        var export = await service.ExportAsync(null, TestContext.Current.CancellationToken);
        data.Channels.Clear();

        // Act
        var verification = await service.VerifyAsync(export.Backup, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(verification.IsValid);
        Assert.Null(Assert.Single(verification.Channels).LocalState);
    }

    [Fact]
    public async Task Given_AnotherNodesBackup_When_Verified_Then_ItIsInvalidWithoutChannels()
    {
        // Arrange
        var other = new BackupTestData(nodeSeed: 9);
        other.AddChannel(1);
        var backup = (await other.CreateService().ExportAsync(null, TestContext.Current.CancellationToken)).Backup;
        var service = new BackupTestData().CreateService();

        // Act
        var verification = await service.VerifyAsync(backup, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(verification.IsValid);
        Assert.Contains("another node", verification.Error);
        Assert.Null(verification.Snapshot);
        Assert.Empty(verification.Channels);
    }

    [Fact]
    public async Task Given_ATamperedBackup_When_Verified_Then_ItIsInvalid()
    {
        // Arrange
        var data = new BackupTestData();
        data.AddChannel(1);
        var service = data.CreateService();
        var backup = (await service.ExportAsync(null, TestContext.Current.CancellationToken)).Backup;
        backup[^1] ^= 0x80;

        // Act
        var verification = await service.VerifyAsync(backup, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(verification.IsValid);
        Assert.NotNull(verification.Error);
    }

    [Fact]
    public async Task Given_ABackupOfAnotherChain_When_Verified_Then_ItIsInvalid()
    {
        // Arrange
        var data = new BackupTestData { Network = BitcoinNetwork.Signet };
        data.AddChannel(1);
        var backup = (await data.CreateService().ExportAsync(null, TestContext.Current.CancellationToken)).Backup;
        data.Network = BitcoinNetwork.Regtest;

        // Act
        var verification = await data.CreateService().VerifyAsync(backup, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(verification.IsValid);
        Assert.Contains("another chain", verification.Error);
    }

    [Fact]
    public async Task Given_AChannelWhoseKeyIndexDerivesOtherKeys_When_Verified_Then_ItIsInvalidAndNamed()
    {
        // Arrange
        var data = new BackupTestData();
        data.AddChannel(1);
        var service = data.CreateService();
        var backup = (await service.ExportAsync(null, TestContext.Current.CancellationToken)).Backup;
        data.Signer.Setup(s => s.GetChannelBasepoints(1u))
            .Returns(new ChannelBasepoints(BackupTestData.Key(0x02, 99, 1), BackupTestData.Key(0x02, 99, 2),
                                           BackupTestData.Key(0x02, 99, 3), BackupTestData.Key(0x02, 99, 4),
                                           BackupTestData.Key(0x02, 99, 5)));

        // Act
        var verification = await service.VerifyAsync(backup, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(verification.IsValid);
        Assert.False(Assert.Single(verification.Channels).KeysMatch);
        Assert.Contains(data.Channels[0].ChannelId.ToString(), verification.Error);
    }

    [Fact]
    public async Task Given_NoFile_When_WriteFile_Then_TheBackupIsWrittenAndDecryptsWithOurKey()
    {
        // Arrange
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        data.AddChannel(1, anchors: true);
        data.AddChannel(2);
        var service = data.CreateService();

        // Act
        var result = await service.WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelBackupWriteOutcome.Written, result.Outcome);
        Assert.Equal(2, result.ChannelCount);
        Assert.Equal(2, service.Decrypt(await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken))
                              .Channels.Count);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public async Task Given_TheSameChannels_When_WrittenAgain_Then_TheFileIsUnchangedEvenAfterARestart()
    {
        // Arrange
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        data.AddChannel(1);
        await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);
        var written = await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken);
        data.Now = data.Now.AddHours(1);

        // Act: a new service (a restart) compares with the file, not only with its memory
        var result = await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelBackupWriteOutcome.Unchanged, result.Outcome);
        Assert.Equal(written, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ANewAndAClosedChannel_When_WriteFile_Then_TheFileFollows()
    {
        // Arrange
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        var first = data.AddChannel(1);
        var service = data.CreateService();
        await service.WriteFileAsync(TestContext.Current.CancellationToken);

        // Act
        data.AddChannel(2);
        var opened = await service.WriteFileAsync(TestContext.Current.CancellationToken);
        first.UpdateState(ChannelState.Closed);
        var closed = await service.WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelBackupWriteOutcome.Written, opened.Outcome);
        Assert.Equal(2, opened.ChannelCount);
        Assert.Equal(ChannelBackupWriteOutcome.Written, closed.Outcome);
        var channel = Assert.Single(
            service.Decrypt(await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken)).Channels);
        Assert.Equal(2, ((byte[])channel.ChannelId)[0]);
    }

    [Fact]
    public async Task Given_AWipedDatabaseAndABackupWithChannels_When_WriteFile_Then_TheFileIsKept()
    {
        // Arrange
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        data.AddChannel(1);
        await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);
        var written = await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken);
        data.Channels.Clear();

        // Act
        var result = await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelBackupWriteOutcome.KeptExisting, result.Outcome);
        Assert.Equal(1, result.ChannelCount);
        Assert.Equal(written, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ABackupOfALostChannelAndANewChannelInAWipedDatabase_When_WriteFile_Then_TheOldFileIsKeptAside()
    {
        // Arrange: the file holds channel 1; the database was wiped and channel 2 opened before any restore
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        data.AddChannel(1);
        await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);
        var written = await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken);
        data.Channels.Clear();
        data.AddChannel(2);
        var service = data.CreateService();

        // Act
        var result = await service.WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert: the new file holds channel 2, the old one (channel 1) is kept byte for byte
        Assert.Equal(ChannelBackupWriteOutcome.Written, result.Outcome);
        var current = Assert.Single(
            service.Decrypt(await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken)).Channels);
        Assert.Equal(2, ((byte[])current.ChannelId)[0]);
        Assert.NotNull(result.MovedAsidePath);
        Assert.EndsWith(".superseded", result.MovedAsidePath);
        Assert.Equal(written, await File.ReadAllBytesAsync(result.MovedAsidePath, TestContext.Current.CancellationToken));
        var kept = Assert.Single(service.Decrypt(written).Channels);
        Assert.Equal(1, ((byte[])kept.ChannelId)[0]);
    }

    [Fact]
    public async Task Given_ASupersededFileTwiceInOneSecond_When_WriteFile_Then_NeitherIsOverwritten()
    {
        // Arrange: channel 1 lost, then channel 2 lost too, at the same clock second
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        data.AddChannel(1);
        await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);
        data.Channels.Clear();
        data.AddChannel(2);
        var first = await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);
        data.Channels.Clear();
        data.AddChannel(3);

        // Act
        var second = await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(first.MovedAsidePath);
        Assert.NotNull(second.MovedAsidePath);
        Assert.NotEqual(first.MovedAsidePath, second.MovedAsidePath);
        Assert.True(File.Exists(first.MovedAsidePath));
        Assert.True(File.Exists(second.MovedAsidePath));
    }

    [Fact]
    public async Task Given_ADirectory_When_SyncedAfterAWrite_Then_TheSyncSucceedsOnUnix()
    {
        // Arrange
        await ChannelBackupFile.WriteAtomicallyAsync(FilePath, new byte[] { 1, 2, 3 },
                                                     TestContext.Current.CancellationToken);

        // Act
        var synced = ChannelBackupFile.TrySyncDirectory(_directory);

        // Assert
        Assert.Equal(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(), synced);
    }

    [Fact]
    public async Task Given_OnlyClosedChannelsInTheDatabase_When_WriteFile_Then_AnEmptyBackupReplacesTheFile()
    {
        // Arrange
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        var channel = data.AddChannel(1);
        var service = data.CreateService();
        await service.WriteFileAsync(TestContext.Current.CancellationToken);
        channel.UpdateState(ChannelState.Closed);

        // Act
        var result = await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelBackupWriteOutcome.Written, result.Outcome);
        Assert.Empty(service.Decrypt(await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken))
                            .Channels);
    }

    [Fact]
    public async Task Given_AFileOfAnotherNode_When_WriteFile_Then_ItIsMovedAsideAndOursWritten()
    {
        // Arrange
        var other = new BackupTestData(nodeSeed: 9);
        other.Options.FilePath = FilePath;
        other.AddChannel(5);
        await other.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);
        var theirs = await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken);
        var data = new BackupTestData();
        data.Options.FilePath = FilePath;
        data.AddChannel(1);

        // Act
        var result = await data.CreateService().WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelBackupWriteOutcome.Written, result.Outcome);
        Assert.NotNull(result.MovedAsidePath);
        Assert.Equal(theirs, await File.ReadAllBytesAsync(result.MovedAsidePath, TestContext.Current.CancellationToken));
        Assert.Single(data.CreateService()
                          .Decrypt(await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken))
                          .Channels);
    }

    [Fact]
    public async Task Given_NoFileConfiguredOrDisabled_When_WriteFile_Then_NothingIsWritten()
    {
        // Arrange
        var data = new BackupTestData();
        data.AddChannel(1);
        var noFile = data.CreateService();
        var disabledData = new BackupTestData();
        disabledData.AddChannel(1);
        disabledData.Options.FilePath = FilePath;
        disabledData.Options.Enabled = false;
        var disabled = disabledData.CreateService();

        // Act
        var first = await noFile.WriteFileAsync(TestContext.Current.CancellationToken);
        var second = await disabled.WriteFileAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ChannelBackupWriteOutcome.Disabled, first.Outcome);
        Assert.Equal(ChannelBackupWriteOutcome.Disabled, second.Outcome);
        Assert.False(File.Exists(FilePath));
    }
}