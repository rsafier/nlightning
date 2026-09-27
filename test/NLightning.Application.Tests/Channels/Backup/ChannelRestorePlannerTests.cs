namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Protocol.ValueObjects;

public class ChannelRestorePlannerTests
{
    [Fact]
    public void Given_ChannelsNotInTheDatabase_When_Planned_Then_EachIsRestoredWithItsBasepoints()
    {
        // Arrange
        var data = new BackupTestData();
        var entries = new[]
        {
            ChannelBackupService.CreateEntry(data.AddChannel(1, anchors: true), data.Peers[0]),
            ChannelBackupService.CreateEntry(data.AddChannel(2), data.Peers[1])
        };

        // Act
        var plan = ChannelRestorePlanner.Plan(entries, _ => (false, null), data.Signer.Object.GetChannelBasepoints);

        // Assert
        Assert.Equal(2, plan.Count);
        Assert.All(plan, item => Assert.Equal(ChannelRestoreAction.Restore, item.Action));
        Assert.Equal(entries[0].LocalPaymentBasepoint, plan[0].LocalBasepoints!.Value.PaymentBasepoint);
        Assert.Equal(entries[1].ChannelId, plan[1].Entry.ChannelId);
    }

    [Fact]
    public void Given_AChannelAlreadyInTheDatabase_When_Planned_Then_LeftAsItIs()
    {
        // Arrange
        var data = new BackupTestData();
        var existing = ChannelBackupService.CreateEntry(data.AddChannel(1), data.Peers[0]);
        var unreadable = ChannelBackupService.CreateEntry(data.AddChannel(2), data.Peers[1]);
        var missing = ChannelBackupService.CreateEntry(data.AddChannel(3), data.Peers[2]);

        // Act
        var plan = ChannelRestorePlanner.Plan([existing, unreadable, missing],
                                              id => id == existing.ChannelId
                                                        ? (true, ChannelState.Open)
                                                        : id == unreadable.ChannelId
                                                            ? (true, null)
                                                            : (false, null),
                                              data.Signer.Object.GetChannelBasepoints);

        // Assert
        Assert.Equal(ChannelRestoreAction.AlreadyExists, plan[0].Action);
        Assert.Equal(ChannelState.Open, plan[0].ExistingState);
        Assert.Equal(ChannelRestoreAction.AlreadyExists, plan[1].Action);
        Assert.Null(plan[1].ExistingState);
        Assert.Equal(ChannelRestoreAction.Restore, plan[2].Action);
    }

    [Fact]
    public void Given_AKeyIndexThatDerivesOtherKeys_When_Planned_Then_KeysMismatch()
    {
        // Arrange: the backup of another key file
        var data = new BackupTestData();
        var entry = ChannelBackupService.CreateEntry(data.AddChannel(1), data.Peers[0]);
        var otherKeys = data.Signer.Object.GetChannelBasepoints(9);

        // Act
        var plan = ChannelRestorePlanner.Plan([entry], _ => (false, null), _ => otherKeys);

        // Assert
        var item = Assert.Single(plan);
        Assert.Equal(ChannelRestoreAction.KeysMismatch, item.Action);
        Assert.Null(item.LocalBasepoints);
    }

    [Fact]
    public void Given_ARepeatedChannelId_When_Planned_Then_RestoredOnce()
    {
        // Arrange
        var data = new BackupTestData();
        var entry = ChannelBackupService.CreateEntry(data.AddChannel(1), data.Peers[0]);

        // Act
        var plan = ChannelRestorePlanner.Plan([entry, entry], _ => (false, null),
                                              data.Signer.Object.GetChannelBasepoints);

        // Assert
        Assert.Equal([ChannelRestoreAction.Restore, ChannelRestoreAction.Duplicate], plan.Select(p => p.Action));
    }

    [Fact]
    public void Given_ABackupOfAnotherChainOrNode_When_Validated_Then_Refused()
    {
        // Arrange
        var snapshot = BackupTestData.SampleSnapshot();
        var nodeId = snapshot.NodeId;

        // Act / Assert
        ChannelRestorePlanner.Validate(snapshot, BitcoinNetwork.Regtest.ChainHash, nodeId);
        var chain = Assert.Throws<ChannelBackupException>(
            () => ChannelRestorePlanner.Validate(snapshot, BitcoinNetwork.Mainnet.ChainHash, nodeId));
        Assert.Contains("another chain", chain.Message);
        var node = Assert.Throws<ChannelBackupException>(
            () => ChannelRestorePlanner.Validate(snapshot, BitcoinNetwork.Regtest.ChainHash,
                                                 BackupTestData.Key(0x02, 1, 1)));
        Assert.Contains("another node", node.Message);
    }

    [Fact]
    public void Given_NoChannels_When_Planned_Then_Empty()
    {
        // Act
        var plan = ChannelRestorePlanner.Plan([], _ => (false, null),
                                              _ => new ChannelBasepoints());

        // Assert
        Assert.Empty(plan);
    }
}