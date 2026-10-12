using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Managers;

using Application.Channels.Managers;
using Application.Channels.Services;
using Application.InteractiveTx.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Wave qit integration (splicing plan IT4-T1): when a peer's connection closes, <c>ChannelManager</c> hands every
/// channel the interactive-tx driver holds for that peer to <see cref="IInteractiveTxDriver.OnDisconnectedAsync"/>
/// under the channel's lock, so an unstored negotiation is forgotten and its wallet reservation released.
/// </summary>
public class ChannelManagerInteractiveTxDisconnectTests
{
    private static readonly CompactPubKey s_peer = new([0x02, .. Enumerable.Repeat((byte)0x11, 32)]);
    private static readonly CompactPubKey s_otherPeer = new([0x03, .. Enumerable.Repeat((byte)0x22, 32)]);
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x77, 32).ToArray());

    [Fact]
    public async Task Given_ANegotiationWithThePeer_When_ThePeerDisconnects_Then_TheDriverIsToldUnderTheChannelLock()
    {
        // Arrange
        var lockProvider = new ChannelLockProvider();
        var heldLock = false;
        var driver = new Mock<IInteractiveTxDriver>();
        driver.Setup(d => d.GetChannels(s_peer)).Returns([s_channelId]);
        driver.Setup(d => d.GetChannels(s_otherPeer)).Returns([]);
        driver.Setup(d => d.OnDisconnectedAsync(s_channelId, It.IsAny<CancellationToken>()))
              .Returns(async () =>
               {
                   // The lock is not reentrant: while the manager holds it, a second acquire does not complete
                   var probe = lockProvider.AcquireAsync(s_channelId);
                   heldLock = !probe.IsCompleted;
                   await Task.CompletedTask;
                   _ = probe.ContinueWith(t => t.Result.Dispose(), TaskScheduler.Default);
               });
        var manager = CreateChannelManager(lockProvider, driver.Object);

        // Act
        await manager.OnPeerDisconnectedAsync(s_otherPeer);
        await manager.OnPeerDisconnectedAsync(s_peer);

        // Assert
        driver.Verify(d => d.OnDisconnectedAsync(s_channelId, It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(heldLock);
    }

    [Fact]
    public async Task Given_NoInteractiveTxDriver_When_ThePeerDisconnects_Then_NothingFails()
    {
        // Arrange
        var manager = CreateChannelManager(new ChannelLockProvider(), null);

        // Act
        var exception = await Record.ExceptionAsync(() => manager.OnPeerDisconnectedAsync(s_peer));

        // Assert
        Assert.Null(exception);
    }

    private static ChannelManager CreateChannelManager(IChannelLockProvider lockProvider,
                                                       IInteractiveTxDriver? driver)
    {
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var services = new ServiceCollection();
        if (driver is not null)
            services.AddSingleton(driver);
        var provider = services.BuildServiceProvider();
        return new ChannelManager(new Mock<IBlockchainMonitor>().Object, lockProvider, memory.Object,
                                  NullLogger<ChannelManager>.Instance, new Mock<ILightningSigner>().Object, provider);
    }
}