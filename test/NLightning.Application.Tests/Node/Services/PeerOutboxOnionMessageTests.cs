using Microsoft.Extensions.Logging;

namespace NLightning.Application.Tests.Node.Services;

using Application.Node.Services;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;

/// <summary>
/// Wave M6 (OM2-T1): onion messages are a bounded, lower-priority class of a peer's outbox. They go out only when
/// nothing else waits, a full onion queue drops (and counts) the next one while channel messages are still accepted,
/// and a disconnect drops the ones still waiting.
/// </summary>
public class PeerOutboxOnionMessageTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    private readonly Mock<IPeerService> _peerService = new();
    private readonly TaskCompletionSource _firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseWire = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<object> _wire = [];
    private int _sends;

    /// <summary>The peer stops reading during the first send (of any kind) until the wire is released.</summary>
    public PeerOutboxOnionMessageTests()
    {
        _peerService.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                    .Returns((IChannelMessage message) => SendAsync(message));
        _peerService.Setup(p => p.SendGossipMessageAsync(It.IsAny<IMessage>()))
                    .Returns((IMessage message) => SendAsync(message));
        _peerService.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>()))
                    .Returns((WarningException warning) => SendAsync(warning));
        _peerService.Setup(p => p.SendErrorAsync(It.IsAny<ErrorMessage>()))
                    .Returns((ErrorMessage error) => SendAsync(error));
        _peerService.Setup(p => p.SendOnionMessageAsync(It.IsAny<OnionMessageMessage>(), It.IsAny<CancellationToken>()))
                    .Returns((OnionMessageMessage message, CancellationToken _) => SendAsync(message));
    }

    [Fact]
    public async Task Given_ChannelMessagesAndOnionMessagesWaiting_When_TheWireFrees_Then_OnionMessagesGoLast()
    {
        // Arrange: the first channel message is on the wire; an onion message is queued before three more items
        var outbox = CreateOutbox();
        var channelMessages = Enumerable.Range(0, 3).Select(_ => new Mock<IChannelMessage>().Object).ToArray();
        var gossip = new Mock<IMessage>().Object;
        var onion = Enumerable.Range(0, 2).Select(_ => CreateOnionMessage()).ToArray();
        Assert.True(outbox.TryEnqueue(channelMessages[0]));
        await _firstSendStarted.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Act
        Assert.True(outbox.TryEnqueueOnionMessage(onion[0]));
        Assert.True(outbox.TryEnqueue(channelMessages[1]));
        Assert.True(outbox.TryEnqueueGossip(gossip));
        Assert.True(outbox.TryEnqueueOnionMessage(onion[1]));
        Assert.True(outbox.TryEnqueue(channelMessages[2]));
        var queuedOnion = outbox.QueuedOnionMessageCount;
        _releaseWire.SetResult();
        await WaitForWireAsync(6);

        // Assert: everything else first, in FIFO order, then the onion messages in theirs
        Assert.Equal(2, queuedOnion);
        Assert.Equal<object>([channelMessages[0], channelMessages[1], gossip, channelMessages[2], onion[0], onion[1]],
                             Snapshot());
        Assert.Equal(0, outbox.QueuedOnionMessageCount);
    }

    [Fact]
    public async Task Given_ContinuousGossip_When_AnOnionMessageWaits_Then_ItGoesOutAfterAtMostTheGossipShare()
    {
        // Arrange: the wire is busy; a long gossip stream is queued with an onion message in the middle
        var outbox = CreateOutbox();
        var first = new Mock<IChannelMessage>().Object;
        Assert.True(outbox.TryEnqueue(first));
        await _firstSendStarted.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        var gossip = Enumerable.Range(0, 3 * PeerOutbox.GossipSendsPerOnionMessage)
                               .Select(_ => new Mock<IMessage>().Object).ToArray();
        var onion = CreateOnionMessage();

        // Act
        foreach (var message in gossip.Take(2))
            Assert.True(outbox.TryEnqueueGossip(message));
        Assert.True(outbox.TryEnqueueOnionMessage(onion));
        foreach (var message in gossip.Skip(2))
            Assert.True(outbox.TryEnqueueGossip(message));
        _releaseWire.SetResult();
        await WaitForWireAsync(gossip.Length + 2);

        // Assert: the onion message takes the place of the next gossip after the gossip share
        var wire = Snapshot();
        Assert.Equal(PeerOutbox.GossipSendsPerOnionMessage + 1, wire.IndexOf(onion));
        Assert.Equal<object>([first, .. gossip.Take(PeerOutbox.GossipSendsPerOnionMessage), onion,
                              .. gossip.Skip(PeerOutbox.GossipSendsPerOnionMessage)], wire);
    }

    [Fact]
    public async Task Given_TheGossipShareIsUsed_When_AChannelMessageIsAtTheHead_Then_ItStillGoesBeforeTheOnionMessage()
    {
        // Arrange
        var outbox = CreateOutbox();
        var first = new Mock<IChannelMessage>().Object;
        Assert.True(outbox.TryEnqueue(first));
        await _firstSendStarted.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        var gossipBefore = Enumerable.Range(0, PeerOutbox.GossipSendsPerOnionMessage)
                                     .Select(_ => new Mock<IMessage>().Object).ToArray();
        var gossipAfter = Enumerable.Range(0, 2).Select(_ => new Mock<IMessage>().Object).ToArray();
        var channelMessage = new Mock<IChannelMessage>().Object;
        var onion = CreateOnionMessage();

        // Act
        foreach (var message in gossipBefore)
            Assert.True(outbox.TryEnqueueGossip(message));
        Assert.True(outbox.TryEnqueueOnionMessage(onion));
        Assert.True(outbox.TryEnqueue(channelMessage));
        foreach (var message in gossipAfter)
            Assert.True(outbox.TryEnqueueGossip(message));
        _releaseWire.SetResult();
        await WaitForWireAsync(gossipBefore.Length + gossipAfter.Length + 3);

        // Assert
        Assert.Equal<object>([first, .. gossipBefore, channelMessage, onion, .. gossipAfter], Snapshot());
    }

    [Fact]
    public async Task Given_OnionMessagesAtTheCap_When_OneMoreIsQueued_Then_ItIsDroppedAndChannelMessagesAreNot()
    {
        // Arrange: cap 2, the wire is busy with a channel message
        var dropped = 0;
        var outbox = CreateOutbox(maxQueuedOnionMessages: 2, onOnionMessageDropped: () => dropped++);
        var first = new Mock<IChannelMessage>().Object;
        Assert.True(outbox.TryEnqueue(first));
        await _firstSendStarted.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        var onion = Enumerable.Range(0, 4).Select(_ => CreateOnionMessage()).ToArray();
        Assert.True(outbox.TryEnqueueOnionMessage(onion[0]));
        Assert.True(outbox.TryEnqueueOnionMessage(onion[1]));

        // Act
        var thirdQueued = outbox.TryEnqueueOnionMessage(onion[2]);
        var channelMessage = new Mock<IChannelMessage>().Object;
        var channelQueued = outbox.TryEnqueue(channelMessage);
        var warningQueued = outbox.TryEnqueueWarning(new WarningException("careful"));
        _releaseWire.SetResult();
        await WaitForWireAsync(5);
        var afterDrainQueued = outbox.TryEnqueueOnionMessage(onion[3]);
        await WaitForWireAsync(6);

        // Assert
        Assert.False(thirdQueued);
        Assert.True(channelQueued);
        Assert.True(warningQueued);
        Assert.True(afterDrainQueued);
        Assert.Equal(1, dropped);
        Assert.Equal(1, outbox.DroppedOnionMessageCount);
        var wire = Snapshot();
        Assert.Same(first, wire[0]);
        Assert.Same(channelMessage, wire[1]);
        Assert.IsType<WarningException>(wire[2]);
        Assert.Same(onion[0], wire[3]);
        Assert.Same(onion[1], wire[4]);
        Assert.Same(onion[3], wire[5]);
        Assert.DoesNotContain(onion[2], wire);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Given_ACapBelowOne_When_OnionMessagesAreQueued_Then_TheQueueStaysBoundedAtOne(int cap)
    {
        // Arrange
        var outbox = CreateOutbox(maxQueuedOnionMessages: cap);
        Assert.True(outbox.TryEnqueue(new Mock<IChannelMessage>().Object));
        await _firstSendStarted.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Act
        var first = outbox.TryEnqueueOnionMessage(CreateOnionMessage());
        var second = outbox.TryEnqueueOnionMessage(CreateOnionMessage());
        _releaseWire.SetResult();

        // Assert
        Assert.True(first);
        Assert.False(second);
        await WaitForWireAsync(2);
    }

    [Fact]
    public async Task Given_OnionMessagesWaiting_When_TheOutboxDisconnects_Then_TheyAreDroppedAndLaterOnesRefused()
    {
        // Arrange
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _peerService.Setup(p => p.Disconnect(It.IsAny<Exception?>())).Callback(() => disconnected.TrySetResult());
        var outbox = CreateOutbox();
        var first = new Mock<IChannelMessage>().Object;
        Assert.True(outbox.TryEnqueue(first));
        await _firstSendStarted.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.True(outbox.TryEnqueueOnionMessage(CreateOnionMessage()));

        // Act
        Assert.True(outbox.TryEnqueueDisconnect(null));
        var afterDisconnect = outbox.TryEnqueueOnionMessage(CreateOnionMessage());
        _releaseWire.SetResult();
        await disconnected.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(afterDisconnect);
        Assert.Equal<object>([first], Snapshot());
        _peerService.Verify(p => p.SendOnionMessageAsync(It.IsAny<OnionMessageMessage>(),
                                                         It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_OnionMessagesWaiting_When_TheOutboxIsCompleted_Then_TheyAreStillSentAfterTheRest()
    {
        // Arrange
        var outbox = CreateOutbox();
        var first = new Mock<IChannelMessage>().Object;
        var second = new Mock<IChannelMessage>().Object;
        var onion = CreateOnionMessage();
        Assert.True(outbox.TryEnqueue(first));
        await _firstSendStarted.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        Assert.True(outbox.TryEnqueueOnionMessage(onion));
        Assert.True(outbox.TryEnqueue(second));

        // Act
        outbox.Complete();
        var afterComplete = outbox.TryEnqueueOnionMessage(CreateOnionMessage());
        _releaseWire.SetResult();
        await outbox.Completion.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(afterComplete);
        Assert.Equal<object>([first, second, onion], Snapshot());
        Assert.Equal(0, outbox.QueuedOnionMessageCount);
    }

    [Fact]
    public async Task Given_AnOnionMessageSendThatFails_When_MoreIsQueued_Then_TheLoopGoesOn()
    {
        // Arrange
        _releaseWire.SetResult();
        var failing = CreateOnionMessage();
        _peerService.Setup(p => p.SendOnionMessageAsync(failing, It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("not negotiated"));
        var outbox = CreateOutbox();
        var channelMessage = new Mock<IChannelMessage>().Object;
        var next = CreateOnionMessage();

        // Act
        Assert.True(outbox.TryEnqueueOnionMessage(failing));
        await WaitUntilAsync(() => outbox.QueuedOnionMessageCount == 0);
        Assert.True(outbox.TryEnqueue(channelMessage));
        Assert.True(outbox.TryEnqueueOnionMessage(next));
        await WaitForWireAsync(2);

        // Assert
        Assert.Equal<object>([channelMessage, next], Snapshot());
    }

    [Fact]
    public void Given_ANullOnionMessage_When_Queued_Then_Throws()
    {
        // Arrange
        var outbox = CreateOutbox();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => outbox.TryEnqueueOnionMessage(null!));
    }

    private PeerOutbox CreateOutbox(int maxQueuedOnionMessages = PeerOutbox.DefaultMaxQueuedOnionMessages,
                                    Action? onOnionMessageDropped = null) =>
        new(_peerService.Object, new Mock<ILogger>().Object, maxQueuedOnionMessages: maxQueuedOnionMessages,
            onOnionMessageDropped: onOnionMessageDropped);

    private async Task SendAsync(object item)
    {
        lock (_wire)
            _wire.Add(item);

        if (Interlocked.Increment(ref _sends) == 1)
        {
            _firstSendStarted.TrySetResult();
            await _releaseWire.Task;
        }
    }

    private List<object> Snapshot()
    {
        lock (_wire)
            return [.. _wire];
    }

    private Task WaitForWireAsync(int count) => WaitUntilAsync(() => Snapshot().Count >= count);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the outbox");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static OnionMessageMessage CreateOnionMessage()
    {
        var pathKey = new byte[33];
        pathKey[0] = 0x02;
        return new OnionMessageMessage(new OnionMessagePayload(new CompactPubKey(pathKey), new byte[1366]));
    }
}