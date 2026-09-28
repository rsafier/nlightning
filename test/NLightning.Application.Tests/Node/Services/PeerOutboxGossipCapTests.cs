using Microsoft.Extensions.Logging;

namespace NLightning.Application.Tests.Node.Services;

using Application.Node.Services;
using Domain.Exceptions;
using Domain.Gossip.Enums;
using Domain.Gossip.Models;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;

/// <summary>
/// NL-360: the gossip share of a peer's outbox is bounded: capped (own and relayed) gossip is refused while the peer
/// has the cap's worth of gossip waiting, channel messages never are, and the FIFO order holds.
/// </summary>
public class PeerOutboxGossipCapTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly Mock<IPeerService> _peerService = new();
    private readonly TaskCompletionSource _firstGossipSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseWire = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<object> _wire = [];

    /// <summary>The peer stops reading after the first gossip message: the send of it hangs until released.</summary>
    public PeerOutboxGossipCapTests()
    {
        _peerService.Setup(p => p.SendGossipMessageAsync(It.IsAny<IMessage>()))
                    .Returns(async (IMessage message) =>
                     {
                         Record(message);
                         _firstGossipSent.TrySetResult();
                         await _releaseWire.Task;
                     });
        _peerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                    .Returns((IChannelMessage message) =>
                     {
                         Record(message);
                         return Task.CompletedTask;
                     });
        _peerService.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>()))
                    .Returns((WarningException warning) =>
                     {
                         Record(warning);
                         return Task.CompletedTask;
                     });
    }

    [Fact]
    public async Task Given_GossipAtTheCap_When_MoreIsQueued_Then_CappedGossipIsRefusedAndChannelMessagesAreNot()
    {
        // Arrange: cap 2; the first gossip message is on the wire (taken off the queue), two more wait
        var dropped = 0;
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, 2,
                                    () => Interlocked.Increment(ref dropped));
        var gossip = Enumerable.Range(0, 5).Select(_ => new Mock<IMessage>().Object).ToArray();
        Assert.True(outbox.TryEnqueueGossip(gossip[0], capped: true));
        await _firstGossipSent.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.True(outbox.TryEnqueueGossip(gossip[1], capped: true));
        Assert.True(outbox.TryEnqueueGossip(gossip[2], capped: true));

        // Act
        var cappedRefused = !outbox.TryEnqueueGossip(gossip[3], capped: true);
        var channelMessage = new Mock<IChannelMessage>().Object;
        var channelQueued = outbox.TryEnqueue(channelMessage);
        var warningQueued = outbox.TryEnqueueWarning(new WarningException("careful"));
        var ownUpdateQueued = outbox.TryEnqueueGossip(gossip[4]);
        var queuedAtTheCap = outbox.QueuedGossipCount;
        _releaseWire.SetResult();
        await WaitForWireAsync(6);

        // Assert: FIFO, without the refused one
        Assert.True(cappedRefused);
        Assert.True(channelQueued);
        Assert.True(warningQueued);
        Assert.True(ownUpdateQueued);
        Assert.Equal(3, queuedAtTheCap);
        Assert.Equal(1, dropped);
        Assert.Equal(1, outbox.RefusedGossipCount);
        Assert.Equal(6, _wire.Count);
        Assert.Same(gossip[0], _wire[0]);
        Assert.Same(gossip[1], _wire[1]);
        Assert.Same(gossip[2], _wire[2]);
        Assert.Same(channelMessage, _wire[3]);
        Assert.IsType<WarningException>(_wire[4]);
        Assert.Same(gossip[4], _wire[5]);
        Assert.DoesNotContain(gossip[3], _wire);
    }

    [Fact]
    public async Task Given_TheQueuedGossipSent_When_MoreIsQueued_Then_ItIsAcceptedAgain()
    {
        // Arrange
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, 1);
        var gossip = Enumerable.Range(0, 4).Select(_ => new Mock<IMessage>().Object).ToArray();
        Assert.True(outbox.TryEnqueueGossip(gossip[0], capped: true));
        await _firstGossipSent.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.True(outbox.TryEnqueueGossip(gossip[1], capped: true));
        Assert.False(outbox.TryEnqueueGossip(gossip[2], capped: true));

        // Act
        _releaseWire.SetResult();
        await WaitForWireAsync(2);
        await WaitUntilAsync(() => outbox.QueuedGossipCount == 0);
        var accepted = outbox.TryEnqueueGossip(gossip[3], capped: true);
        await WaitForWireAsync(3);

        // Assert
        Assert.True(accepted);
        Assert.Same(gossip[3], _wire[2]);
    }

    [Fact]
    public async Task Given_NoCap_When_MuchGossipWaits_Then_EveryMessageIsAccepted()
    {
        // Arrange
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object);
        Assert.True(outbox.TryEnqueueGossip(new Mock<IMessage>().Object, capped: true));
        await _firstGossipSent.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Act
        var accepted = Enumerable.Range(0, 100).All(_ => outbox.TryEnqueueGossip(new Mock<IMessage>().Object, true));

        // Assert
        Assert.True(accepted);
        Assert.Equal(100, outbox.QueuedGossipCount);
        _releaseWire.SetResult();
    }

    [Fact]
    public void Given_AClosedOutbox_When_GossipIsQueued_Then_RefusedWithoutCountingIt()
    {
        // Arrange
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, 10);
        outbox.Complete();

        // Act
        var queued = outbox.TryEnqueueGossip(new Mock<IMessage>().Object, capped: true);

        // Assert
        Assert.False(queued);
        Assert.Equal(0, outbox.QueuedGossipCount);
        Assert.Equal(0, outbox.RefusedGossipCount);
    }

    [Fact]
    public async Task Given_AByteCap_When_GossipWouldExceedIt_Then_ItIsFullAndSmallerGossipStillFits()
    {
        // Arrange: 1,000 bytes; the first message is on the wire (off the queue)
        var refused = 0;
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, 0,
                                    () => Interlocked.Increment(ref refused), maxQueuedGossipBytes: 1_000);
        Assert.Equal(GossipEnqueueResult.Queued, outbox.EnqueueGossip(new Mock<IMessage>().Object, 600));
        await _firstGossipSent.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Act
        var first = outbox.EnqueueGossip(new Mock<IMessage>().Object, 600);
        var tooMuch = outbox.EnqueueGossip(new Mock<IMessage>().Object, 600);
        var small = outbox.EnqueueGossip(new Mock<IMessage>().Object, 300);
        var channelQueued = outbox.TryEnqueue(new Mock<IChannelMessage>().Object);
        var depth = outbox.GossipDepth;
        _releaseWire.SetResult();
        await WaitUntilAsync(() => outbox.QueuedGossipCount == 0);

        // Assert
        Assert.Equal(GossipEnqueueResult.Queued, first);
        Assert.Equal(GossipEnqueueResult.Full, tooMuch);
        Assert.Equal(GossipEnqueueResult.Queued, small);
        Assert.True(channelQueued);
        Assert.Equal(new GossipOutboxDepth(2, 900, 0, 1_000, 1), depth);
        Assert.Equal(1, refused);
        Assert.Equal(0, outbox.QueuedGossipBytes);
        Assert.Equal(3, outbox.SentGossipCount);
    }

    [Fact]
    public async Task Given_AMessageLargerThanTheByteCap_When_TheGossipShareIsEmpty_Then_ItIsQueuedAlone()
    {
        // Arrange: a node_announcement larger than the whole byte cap must not block the connection for good
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, maxQueuedGossipBytes: 1_000);
        Assert.Equal(GossipEnqueueResult.Queued, outbox.EnqueueGossip(new Mock<IMessage>().Object, 10));
        await _firstGossipSent.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Act
        var large = outbox.EnqueueGossip(new Mock<IMessage>().Object, 5_000);
        var next = outbox.EnqueueGossip(new Mock<IMessage>().Object, 10);
        _releaseWire.SetResult();

        // Assert
        Assert.Equal(GossipEnqueueResult.Queued, large);
        Assert.Equal(GossipEnqueueResult.Full, next);
    }

    [Fact]
    public void Given_AClosedOutbox_When_CappedGossipIsQueued_Then_ItIsGoneNotFull()
    {
        // Arrange
        var outbox = new PeerOutbox(_peerService.Object, new Mock<ILogger>().Object, 10, maxQueuedGossipBytes: 100);
        outbox.Complete();

        // Act
        var result = outbox.EnqueueGossip(new Mock<IMessage>().Object, 50);

        // Assert
        Assert.Equal(GossipEnqueueResult.Gone, result);
        Assert.Equal(new GossipOutboxDepth(0, 0, 10, 100, 0), outbox.GossipDepth);
    }

    private void Record(object item)
    {
        lock (_wire)
            _wire.Add(item);
    }

    private Task WaitForWireAsync(int count) =>
        WaitUntilAsync(() =>
        {
            lock (_wire)
                return _wire.Count >= count;
        });

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(s_timeout);
        while (!condition())
            await Task.Delay(5, timeout.Token);
    }
}