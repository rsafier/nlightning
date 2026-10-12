using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Application.Channels.Backup.Models;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;

public class ChannelBackupMonitorTests
{
    private readonly Mock<IChannelBackupService> _service = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly BackupTestData _data = new();
    private int _writes;

    public ChannelBackupMonitorTests()
    {
        _service.Setup(s => s.WriteFileAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref _writes))
                .ReturnsAsync(new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Written, 0, "x"));
    }

    private ChannelBackupMonitor CreateMonitor(string? filePath = "channel.backup") =>
        new(_service.Object, _memory.Object,
            Microsoft.Extensions.Options.Options.Create(new ChannelBackupOptions
            {
                FilePath = filePath,
                WriteDelay = TimeSpan.Zero,
                RefreshInterval = TimeSpan.Zero
            }), TimeProvider.System, NullLogger<ChannelBackupMonitor>.Instance);

    private void RaiseUpdated(Domain.Channels.Models.ChannelModel channel) =>
        _memory.Raise(m => m.OnChannelUpdated += null, _memory.Object, new ChannelUpdatedEventArgs(channel));

    [Fact]
    public async Task Given_AFileConfigured_When_Started_Then_TheFileIsWrittenOnce()
    {
        // Arrange
        await using var monitor = CreateMonitor();

        // Act
        monitor.Start();
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Equal(1, _writes);
    }

    [Fact]
    public async Task Given_NoFileConfigured_When_Started_Then_NothingIsWrittenAndNoEventIsFollowed()
    {
        // Arrange
        await using var monitor = CreateMonitor(filePath: null);

        // Act
        monitor.Start();
        RaiseUpdated(_data.AddChannel(1));
        await monitor.WhenIdleAsync();

        // Assert
        Assert.False(monitor.IsActive);
        Assert.Equal(0, _writes);
    }

    [Fact]
    public async Task Given_AStartedMonitor_When_AChannelOpensThenOnlyItsHtlcsChange_Then_OnlyTheOpenIsWritten()
    {
        // Arrange
        await using var monitor = CreateMonitor();
        monitor.Start();
        await monitor.WhenIdleAsync();
        var channel = _data.AddChannel(1);

        // Act
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();
        RaiseUpdated(channel);
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Equal(2, _writes);
    }

    [Fact]
    public async Task Given_ABackedUpChannel_When_ItGetsItsScidAndThenCloses_Then_EachChangeIsWritten()
    {
        // Arrange
        await using var monitor = CreateMonitor();
        monitor.Start();
        var channel = _data.AddChannel(1);
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();
        var before = _writes;

        // Act
        channel.ShortChannelId = new ShortChannelId(800, 2, 1);
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();
        channel.UpdateState(ChannelState.Closed);
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Equal(before + 2, _writes);
    }

    [Fact]
    public async Task Given_AStartedMonitor_When_AChannelIsUpgraded_Then_TheFileIsWritten()
    {
        // Arrange
        await using var monitor = CreateMonitor();
        monitor.Start();
        await monitor.WhenIdleAsync();

        // Act
        _memory.Raise(m => m.OnChannelUpgraded += null, _memory.Object,
                      new ChannelUpgradedEventArgs(new ChannelId(new byte[32]),
                                                   new ChannelId(Enumerable.Repeat((byte)1, 32).ToArray())));
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Equal(2, _writes);
    }

    [Fact]
    public async Task Given_AFailingWrite_When_TheNextChangeComes_Then_ItIsWrittenAgain()
    {
        // Arrange
        _service.SetupSequence(s => s.WriteFileAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("disk full"))
                .ReturnsAsync(new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Written, 1, "x"));
        await using var monitor = CreateMonitor();
        monitor.Start();
        await monitor.WhenIdleAsync();

        // Act
        RaiseUpdated(_data.AddChannel(1));
        await monitor.WhenIdleAsync();

        // Assert
        _service.Verify(s => s.WriteFileAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Given_AStoppedMonitor_When_AChannelChanges_Then_NothingIsWritten()
    {
        // Arrange
        var monitor = CreateMonitor();
        monitor.Start();
        await monitor.WhenIdleAsync();

        // Act
        await monitor.StopAsync();
        RaiseUpdated(_data.AddChannel(1));
        monitor.RequestWrite();
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Equal(1, _writes);
        await monitor.DisposeAsync();
    }
}