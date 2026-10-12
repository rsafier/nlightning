namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Domain.Node.Models;

/// <summary>
/// The restore's addresses after the NL-497 review: the row of a peer that connected to us from a loopback address is
/// inbound-only, and its host (loopback on rows written before the fix) is never dialed, it may be our own listener.
/// </summary>
public partial class ChannelRestoreServiceTests
{
    [Fact]
    public async Task Given_AnInboundOnlyPeerRow_When_GetAddresses_Then_OnlyTheBackupsAddressesAreDialed()
    {
        // Arrange
        var data = new BackupTestData();
        var entry = ChannelBackupService.CreateEntry(data.AddChannel(1), data.Peers[0]);
        _storedPeers.Add(new PeerModel(entry.RemoteNodeId, "127.0.0.1", 9735, "IPv4") { IsInboundOnly = true });
        var service = CreateService();

        // Act
        var addresses = await service.GetAddressesAsync(entry.RemoteNodeId, entry);

        // Assert
        Assert.Equal(["10.0.0.1:9736"], addresses);
    }

    [Fact]
    public async Task Given_ADialablePeerRow_When_GetAddresses_Then_ItIsDialedBeforeTheBackupsAddresses()
    {
        // Arrange
        var data = new BackupTestData();
        var entry = ChannelBackupService.CreateEntry(data.AddChannel(1), data.Peers[0]);
        _storedPeers.Add(new PeerModel(entry.RemoteNodeId, "10.0.0.9", 9735, "IPv4"));
        var service = CreateService();

        // Act
        var addresses = await service.GetAddressesAsync(entry.RemoteNodeId, entry);

        // Assert
        Assert.Equal(["10.0.0.9:9735", "10.0.0.1:9736"], addresses);
    }
}