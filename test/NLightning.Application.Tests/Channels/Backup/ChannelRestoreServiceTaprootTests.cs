namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.Transactions.Enums;

/// <summary>
/// NL-877 T5: a static channel backup of a simple taproot channel (version 2) restores as a taproot recovery channel,
/// next to the anchors and static_remotekey channels of the same backup.
/// </summary>
public partial class ChannelRestoreServiceTests
{
    [Fact]
    public async Task Given_ABackupWithATaprootChannel_When_Restored_Then_TheRecoveryChannelKeepsTheTaprootType()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var source = new BackupTestData();
        source.AddChannel(1, simpleTaproot: true);
        source.AddChannel(2, anchors: true);
        var backup = (await source.CreateService().ExportAsync(null, ct)).Backup;
        var service = CreateService();

        // Act
        var result = await service.RestoreAsync(backup, ct);

        // Assert: both restored as recovery channels; the taproot one keeps its commitment format
        Assert.Equal(2, result.RestoredCount);
        Assert.All(result.Channels, c => Assert.Equal(ChannelRestoreAction.Restore, c.Action));
        Assert.True(result.Channels[0].Entry.OptionSimpleTaproot);
        Assert.All(_storedChannels, c => Assert.True(RecoveryChannels.IsRecoveryChannel(c)));
        Assert.Equal([CommitmentFormat.SimpleTaproot, CommitmentFormat.Anchors],
                     _storedChannels.Select(c => c.ChannelParams.CommitmentFormat));
    }
}