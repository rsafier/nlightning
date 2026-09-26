using Microsoft.Extensions.Logging;

namespace NLightning.Application.Tests.Node.Services;

using Application.Node.Services;
using Domain.Exceptions;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

public class PeerOutboxTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly Mock<IPeerService> _mockPeerService = new();
    private readonly List<string> _wire = [];

    public PeerOutboxTests()
    {
        _mockPeerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                        .Returns(async (IChannelMessage message) =>
                         {
                             await Task.Delay(10);
                             Record($"message:{message.GetHashCode()}");
                         });
        _mockPeerService.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>()))
                        .Returns((WarningException warning) =>
                         {
                             Record($"warning:{warning.Message}");
                             return Task.CompletedTask;
                         });
        _mockPeerService.Setup(p => p.Disconnect(It.IsAny<Exception?>()))
                        .Callback((Exception? reason) => Record($"disconnect:{reason?.Message}"));
    }

    [Fact]
    public async Task Given_MessagesWarningAndDisconnect_When_Enqueued_Then_SentInEnqueueOrder()
    {
        // Arrange
        var outbox = new PeerOutbox(_mockPeerService.Object, new Mock<ILogger>().Object);
        var first = new Mock<IChannelMessage>().Object;
        var second = new Mock<IChannelMessage>().Object;

        // Act
        Assert.True(outbox.TryEnqueue(first));
        Assert.True(outbox.TryEnqueueWarning(new WarningException("careful")));
        Assert.True(outbox.TryEnqueue(second));
        Assert.True(outbox.TryEnqueueDisconnect(new ChannelErrorException("bye")));
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new[]
        {
            $"message:{first.GetHashCode()}", "warning:careful", $"message:{second.GetHashCode()}", "disconnect:bye"
        }, _wire);
    }

    [Fact]
    public async Task Given_AnError_When_Enqueued_Then_SentInOrderWithoutClosingTheOutbox()
    {
        // Arrange - a failed channel's error keeps the connection (BOLT 1 MAY close, we don't)
        _mockPeerService.Setup(p => p.SendErrorAsync(It.IsAny<ErrorMessage>()))
                        .Returns((ErrorMessage error) =>
                         {
                             Record($"error:{error.GetHashCode()}");
                             return Task.CompletedTask;
                         });
        var outbox = new PeerOutbox(_mockPeerService.Object, new Mock<ILogger>().Object);
        var before = new Mock<IChannelMessage>().Object;
        var error = new ErrorMessage(new ErrorPayload("channel failed"));
        var after = new Mock<IChannelMessage>().Object;

        // Act
        Assert.True(outbox.TryEnqueue(before));
        Assert.True(outbox.TryEnqueueError(error));
        Assert.True(outbox.TryEnqueue(after));
        outbox.Complete();
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new[]
        {
            $"message:{before.GetHashCode()}", $"error:{error.GetHashCode()}", $"message:{after.GetHashCode()}"
        }, _wire);
        _mockPeerService.Verify(p => p.Disconnect(It.IsAny<Exception?>()), Times.Never);
    }

    [Fact]
    public async Task Given_ChannelMessageThenGossip_When_Enqueued_Then_GossipIsSentAfterItAsGossip()
    {
        // Arrange - our channel_update must follow the channel_ready queued before it
        _mockPeerService.Setup(p => p.SendGossipMessageAsync(It.IsAny<IMessage>()))
                        .Returns((IMessage message) =>
                         {
                             Record($"gossip:{message.GetHashCode()}");
                             return Task.CompletedTask;
                         });
        var outbox = new PeerOutbox(_mockPeerService.Object, new Mock<ILogger>().Object);
        var channelReady = new Mock<IChannelMessage>().Object;
        var channelUpdate = new Mock<IMessage>().Object;

        // Act
        Assert.True(outbox.TryEnqueue(channelReady));
        Assert.True(outbox.TryEnqueueGossip(channelUpdate));
        outbox.Complete();
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new[] { $"message:{channelReady.GetHashCode()}", $"gossip:{channelUpdate.GetHashCode()}" },
                     _wire);
    }

    [Fact]
    public async Task Given_DisconnectEnqueued_When_MoreIsEnqueued_Then_ItIsRefusedAndNeverSent()
    {
        // Arrange
        var outbox = new PeerOutbox(_mockPeerService.Object, new Mock<ILogger>().Object);

        // Act
        outbox.TryEnqueueDisconnect(null);
        var accepted = outbox.TryEnqueue(new Mock<IChannelMessage>().Object);
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(accepted);
        Assert.Equal(new[] { "disconnect:" }, _wire);
        _mockPeerService.Verify(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()), Times.Never);
    }

    [Fact]
    public async Task Given_SendFails_When_MoreIsQueued_Then_TheRestIsStillSent()
    {
        // Arrange
        var failing = new Mock<IChannelMessage>().Object;
        var next = new Mock<IChannelMessage>().Object;
        _mockPeerService.Setup(p => p.SendMessageAsync(failing)).ThrowsAsync(new InvalidOperationException("boom"));
        var outbox = new PeerOutbox(_mockPeerService.Object, new Mock<ILogger>().Object);

        // Act
        outbox.TryEnqueue(failing);
        outbox.TryEnqueue(next);
        outbox.Complete();
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new[] { $"message:{next.GetHashCode()}" }, _wire);
    }

    private void Record(string entry)
    {
        lock (_wire)
            _wire.Add(entry);
    }
}