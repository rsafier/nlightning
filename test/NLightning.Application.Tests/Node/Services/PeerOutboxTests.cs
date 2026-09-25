using Microsoft.Extensions.Logging;

namespace NLightning.Application.Tests.Node.Services;

using Application.Node.Services;
using Domain.Exceptions;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;

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