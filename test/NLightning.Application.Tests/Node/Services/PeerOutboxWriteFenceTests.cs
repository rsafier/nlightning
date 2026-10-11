using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Node.Services;

using Application.Node.Services;
using Domain.Exceptions;
using Domain.Node.Fencing;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;

/// <summary>
/// NL-1341: the node write fence is asked before every send of a peer's outbox. A refusal sends nothing, closes the
/// outbox and the connection; a fence that lets sends through changes nothing.
/// </summary>
public class PeerOutboxWriteFenceTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly Mock<IPeerService> _peerService = new();
    private readonly List<string> _wire = [];
    private readonly List<Exception?> _disconnects = [];

    public PeerOutboxWriteFenceTests()
    {
        _peerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                    .Returns(() =>
                     {
                         Record("message");
                         return Task.CompletedTask;
                     });
        _peerService.Setup(p => p.SendGossipMessageAsync(It.IsAny<IMessage>()))
                    .Returns(() =>
                     {
                         Record("gossip");
                         return Task.CompletedTask;
                     });
        _peerService.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>()))
                    .Returns(() =>
                     {
                         Record("warning");
                         return Task.CompletedTask;
                     });
        _peerService.Setup(p => p.Disconnect(It.IsAny<Exception?>()))
                    .Callback((Exception? reason) =>
                     {
                         lock (_disconnects)
                             _disconnects.Add(reason);
                     });
    }

    [Fact]
    public async Task Given_ARefusingFence_When_MessagesAreQueued_Then_NothingIsSentAndTheConnectionIsClosed()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, writeFence: fence);

        // Act
        Assert.True(outbox.TryEnqueue(new Mock<IChannelMessage>().Object));
        outbox.TryEnqueueWarning(new WarningException("careful"));
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert: nothing on the wire, one disconnect with the fence's refusal, the outbox refuses new messages
        Assert.Empty(_wire);
        var reason = Assert.Single(_disconnects);
        Assert.IsType<NodeFencedException>(reason);
        Assert.Equal([NodeEffect.PeerSend], fence.Effects);
        Assert.False(outbox.TryEnqueue(new Mock<IChannelMessage>().Object));
    }

    [Fact]
    public async Task Given_ARefusingFence_When_GossipIsQueued_Then_ItIsNotSentAndNotCountedAsQueued()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, writeFence: fence);

        // Act
        Assert.True(outbox.TryEnqueueGossip(new Mock<IMessage>().Object, capped: true));
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_wire);
        Assert.Single(_disconnects);
        Assert.Equal(0, outbox.QueuedGossipCount);
        Assert.Equal(0, outbox.SentGossipCount);
    }

    [Fact]
    public async Task Given_AFenceThatAllowsSends_When_MessagesAreQueued_Then_EachIsCheckedAndSentInOrder()
    {
        // Arrange
        var fence = new FakeNodeWriteFence();
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, writeFence: fence);

        // Act
        Assert.True(outbox.TryEnqueue(new Mock<IChannelMessage>().Object));
        Assert.True(outbox.TryEnqueueGossip(new Mock<IMessage>().Object));
        Assert.True(outbox.TryEnqueueWarning(new WarningException("careful")));
        Assert.True(outbox.TryEnqueueDisconnect(null));
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert: one check per send (none for the disconnect), the wire unchanged
        Assert.Equal(["message", "gossip", "warning"], _wire);
        Assert.Equal([NodeEffect.PeerSend, NodeEffect.PeerSend, NodeEffect.PeerSend], fence.Effects);
        Assert.Single(_disconnects);
    }

    [Fact]
    public async Task Given_AFenceThatStartsRefusing_When_ASendFollows_Then_OnlyTheEarlierSendWentOut()
    {
        // Arrange: the first send is allowed, then the fence refuses (another instance took over)
        var fence = new FakeNodeWriteFence();
        var firstSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _peerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                    .Returns(() =>
                     {
                         Record("message");
                         fence.Refuse = true;
                         firstSent.TrySetResult();
                         return Task.CompletedTask;
                     });
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, writeFence: fence);

        // Act
        Assert.True(outbox.TryEnqueue(new Mock<IChannelMessage>().Object));
        await firstSent.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.True(outbox.TryEnqueue(new Mock<IChannelMessage>().Object));
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["message"], _wire);
        Assert.IsType<NodeFencedException>(Assert.Single(_disconnects));
    }

    private void Record(string entry)
    {
        lock (_wire)
            _wire.Add(entry);
    }
}