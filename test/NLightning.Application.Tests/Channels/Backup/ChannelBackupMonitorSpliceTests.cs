using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;

/// <summary>
/// The backup file follows splices and dual-funded opens (lane SP2-E, NL-478): a locked splice (or the RBF of a
/// dual-funded open) moves the channel's funding, which requests a write; the open itself does too.
/// </summary>
public class ChannelBackupMonitorSpliceTests
{
    private readonly Mock<IChannelBackupService> _service = new();
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly BackupTestData _data = new();
    private int _writes;

    public ChannelBackupMonitorSpliceTests()
    {
        _service.Setup(s => s.WriteFileAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref _writes))
                .ReturnsAsync(new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Written, 0, "x"));
    }

    [Fact]
    public async Task Given_ABackedUpChannel_When_ASpliceLocks_Then_TheFileIsWrittenAgain()
    {
        // Arrange
        await using var monitor = await StartAsync();
        var channel = _data.AddChannel(1, scid: new ShortChannelId(700, 1, 0));
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();
        var before = _writes;

        // Act: the lock moves the funding (new outpoint, rotated key) and the short channel id
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_300_000),
                                                           SplicedChannelBackupTests.FundingKey(1, 1),
                                                           channel.RemoteFundingPubKey!.Value,
                                                           Enumerable.Repeat((byte)0xD1, 32).ToArray(), 1));
        channel.ShortChannelId = new ShortChannelId(760, 4, 1);
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();

        // Assert: one write for the lock, none for the repeated event
        Assert.Equal(before + 1, _writes);
    }

    [Fact]
    public async Task Given_ABackedUpChannel_When_OnlyTheFundingOutpointMoves_Then_TheFileIsWrittenAgain()
    {
        // Arrange: a dual-funded open replaced by RBF before it confirmed (no short channel id yet, same keys)
        await using var monitor = await StartAsync();
        var channel = _data.AddChannel(2, version: ChannelVersion.V2);
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();
        var before = _writes;

        // Act
        channel.ReplaceFundingOutput(new FundingOutputInfo(channel.FundingOutput!.Amount,
                                                           channel.FundingOutput.LocalFundingPubKey,
                                                           channel.FundingOutput.RemoteFundingPubKey,
                                                           Enumerable.Repeat((byte)0xD2, 32).ToArray(), 0));
        RaiseUpdated(channel);
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Equal(before + 1, _writes);
    }

    [Fact]
    public async Task Given_ADualFundedOpen_When_TheChannelIsUpgradedToItsV2Id_Then_TheFileIsWritten()
    {
        // Arrange
        await using var monitor = await StartAsync();
        var before = _writes;
        var channel = _data.AddChannel(3, version: ChannelVersion.V2, state: ChannelState.V1FundingSigned);

        // Act
        _memory.Raise(m => m.OnChannelUpgraded += null, _memory.Object,
                      new ChannelUpgradedEventArgs(new ChannelId(new byte[32]), channel.ChannelId));
        await monitor.WhenIdleAsync();

        // Assert
        Assert.Equal(before + 1, _writes);
    }

    [Fact]
    public void Given_TwoChannelsDifferingOnlyInTheirFunding_When_Keyed_Then_TheKeysDiffer()
    {
        // Arrange
        var channel = _data.AddChannel(4);
        var before = ChannelBackupMonitor.GetBackupKey(channel);

        // Act
        channel.ReplaceFundingOutput(new FundingOutputInfo(channel.FundingOutput!.Amount,
                                                           SplicedChannelBackupTests.FundingKey(4, 1),
                                                           channel.FundingOutput.RemoteFundingPubKey,
                                                           channel.FundingOutput.TransactionId!.Value,
                                                           channel.FundingOutput.Index!.Value));

        // Assert: the rotated key alone changes it
        Assert.NotEqual(before, ChannelBackupMonitor.GetBackupKey(channel));
    }

    private async Task<ChannelBackupMonitor> StartAsync()
    {
        var monitor = new ChannelBackupMonitor(_service.Object, _memory.Object,
                                               Microsoft.Extensions.Options.Options.Create(new ChannelBackupOptions
                                               {
                                                   FilePath = "channel.backup",
                                                   WriteDelay = TimeSpan.Zero,
                                                   RefreshInterval = TimeSpan.Zero
                                               }), TimeProvider.System, NullLogger<ChannelBackupMonitor>.Instance);
        monitor.Start();
        await monitor.WhenIdleAsync();
        return monitor;
    }

    private void RaiseUpdated(ChannelModel channel) =>
        _memory.Raise(m => m.OnChannelUpdated += null, _memory.Object, new ChannelUpdatedEventArgs(channel));
}