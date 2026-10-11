using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.KeyIndexes;
using Domain.Channels.ValueObjects;
using Domain.Protocol.ValueObjects;

/// <summary>
/// NL-1392: at a start, the key index is raised past the channels of the node's own <c>channel.backup</c> that the
/// database lacks.
/// </summary>
public sealed class BackupFileKeyIndexGuardTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"nltg-1392-{Guid.NewGuid():N}.backup");
    private readonly BackupTestData _node = new();
    private readonly Mock<IChannelKeyIndexReserver> _reserver = new();
    private readonly Mock<IChannelDbRepository> _channels = new();
    private readonly ChannelKeyIndexGate _gate = new();
    private readonly List<(ChannelId, uint, ChannelState)> _rows = [];

    public BackupFileKeyIndexGuardTests()
    {
        _reserver.Setup(r => r.ReserveThroughAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((uint index, CancellationToken _) => index);
        _channels.Setup(c => c.GetLocalKeyIndexesAsync()).ReturnsAsync(() => _rows.ToList());
    }

    public void Dispose()
    {
        if (File.Exists(_file))
            File.Delete(_file);
    }

    [Fact]
    public async Task Given_ABackupWithChannelsTheDatabaseLacks_When_Started_Then_TheKeyIndexIsRaisedPastThem()
    {
        // Arrange: channels 1, 4 and 2 backed up; 4 is still in the database
        var ct = TestContext.Current.CancellationToken;
        await WriteBackupAsync(ct, 1, 4, 2);
        _rows.Add((new ChannelId(Enumerable.Repeat((byte)4, 32).ToArray()), 4u, ChannelState.Open));

        // Act
        var raised = await Guard().RaiseAsync(_file, BitcoinNetwork.Regtest.ChainHash, _node.KeyManager.NodeId,
                                             _channels.Object, ct);

        // Assert
        Assert.Equal(2u, raised);
        _reserver.Verify(r => r.ReserveThroughAsync(2u, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(_gate.HoldReason);
    }

    [Fact]
    public async Task Given_NoFileOrEveryChannelStored_When_Started_Then_NothingIsRaised()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var guard = Guard();

        // Act
        var withoutFile = await guard.RaiseAsync(_file, BitcoinNetwork.Regtest.ChainHash, _node.KeyManager.NodeId,
                                                 _channels.Object, ct);
        await WriteBackupAsync(ct, 3);
        _rows.Add((new ChannelId(Enumerable.Repeat((byte)3, 32).ToArray()), 3u, ChannelState.Open));
        var allStored = await guard.RaiseAsync(_file, BitcoinNetwork.Regtest.ChainHash, _node.KeyManager.NodeId,
                                               _channels.Object, ct);

        // Assert
        Assert.Null(withoutFile);
        Assert.Null(allStored);
        _reserver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ABackupOfAnotherChain_When_Started_Then_ItIsIgnored()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await WriteBackupAsync(ct, 1);

        // Act
        var raised = await Guard().RaiseAsync(_file, BitcoinNetwork.Mainnet.ChainHash, _node.KeyManager.NodeId,
                                             _channels.Object, ct);

        // Assert
        Assert.Null(raised);
        _reserver.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_ARaiseThatFails_When_Started_Then_OpensAreHeld()
    {
        // Arrange: fail closed
        var ct = TestContext.Current.CancellationToken;
        await WriteBackupAsync(ct, 5);
        _reserver.Setup(r => r.ReserveThroughAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new IOException("key file"));

        // Act
        var raised = await Guard().RaiseAsync(_file, BitcoinNetwork.Regtest.ChainHash, _node.KeyManager.NodeId,
                                             _channels.Object, ct);

        // Assert
        Assert.Null(raised);
        Assert.Equal(ChannelKeyIndexHold.AwaitingRestoreReason, _gate.HoldReason);
    }

    private BackupFileKeyIndexGuard Guard() =>
        new(_node.CreateService(), _reserver.Object, _gate, NullLogger.Instance);

    private async Task WriteBackupAsync(CancellationToken ct, params byte[] tags)
    {
        var source = new BackupTestData();
        foreach (var tag in tags)
            source.AddChannel(tag);
        await File.WriteAllBytesAsync(_file, (await source.CreateService().ExportAsync(null, ct)).Backup, ct);
    }
}