using System.Collections.Concurrent;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Infrastructure.Protocol.Models;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Node.Managers;

using Application.Node.Managers;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Node.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Transport;
using Infrastructure.Node.ValueObjects;
using Infrastructure.Transport.Interfaces;

/// <summary>
/// The per-channel lanes of a connection (<c>Node:ChannelLanes</c>, plan
/// <c>docs/agents/PEER_CHANNEL_PARALLELISM_PLAN.md</c> §3.1): each registered channel's messages are handled in arrival
/// order on its own lane, different channels at the same time, and everything else serially on the peer lane, with the
/// caps, the backpressure and the connection rules of the plain loop.
/// </summary>
public class PeerManagerLaneTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_stableWindow = TimeSpan.FromMilliseconds(300);

    private static readonly CompactPubKey s_ourNodeKey =
        new PubKey("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb").ToBytes();

    private readonly CompactPubKey _peerKey =
        new PubKey("028d7500dd4c12685d1f568b4c2b5048e8534b873319f3a8daa612b469132ec7f7").ToBytes();

    private readonly Mock<IChannelManager> _channelManager = new();
    private readonly Mock<IPeerServiceFactory> _peerServiceFactory = new();
    private readonly Mock<ITcpService> _tcpService = new();
    private readonly FakeServiceProvider _serviceProvider = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IPeerDbRepository> _peerDbRepository = new();
    private readonly Mock<IChannelMemoryRepository> _channelMemory = new();
    private readonly Mock<ISecureKeyManager> _keyManager = new();
    private readonly Queue<Mock<IPeerService>> _connections = new();
    private readonly Mock<IPeerService> _connection = new();

    // What the channel manager was handed, in completion order, and what it is handling right now
    private readonly List<UpdateFeeMessage> _handled = [];
    private readonly ConcurrentDictionary<ChannelId, int> _inFlightPerChannel = new();
    private readonly List<string> _events = [];
    private int _inFlight;
    private int _sameChannelOverlaps;
    private int _maxInFlight;

    // A handler waits on the gate of its message (none: returns at once)
    private readonly ConcurrentDictionary<UpdateFeeMessage, TaskCompletionSource> _gates = new();
    private readonly ConcurrentDictionary<UpdateFeeMessage, TaskCompletionSource> _started = new();
    private readonly ConcurrentDictionary<UpdateFeeMessage, Exception> _failures = new();
    private readonly ConcurrentDictionary<UpdateFeeMessage, IChannelMessage> _replies = new();
    private readonly List<IChannelMessage> _sent = [];
    private readonly TaskCompletionSource<Exception?> _disconnect =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Func<int>? _handlerDelayMs;
    private uint _feerate = 1_000;

    public PeerManagerLaneTests()
    {
        SetupConnection(_connection);
        _connections.Enqueue(_connection);
        _peerServiceFactory
           .Setup(f => f.CreateConnectedPeerAsync(It.IsAny<CompactPubKey>(), It.IsAny<PeerConnection>()))
           .ReturnsAsync(() => _connections.Dequeue().Object);

        _channelManager
           .Setup(cm => cm.HandleChannelMessageAsync(It.IsAny<IChannelMessage>(), It.IsAny<FeatureOptions>(),
                                                     It.IsAny<CompactPubKey>()))
           .Returns((IChannelMessage message, FeatureOptions _, CompactPubKey _) =>
                        HandleAsync((UpdateFeeMessage)message));
        _channelManager.Setup(cm => cm.OnPeerConnectedAsync(It.IsAny<CompactPubKey>()))
                       .ReturnsAsync(() =>
                        {
                            Event("connected");
                            return [];
                        });
        _channelManager.Setup(cm => cm.OnPeerDisconnectedAsync(It.IsAny<CompactPubKey>()))
                       .Returns(() =>
                        {
                            Event("reverted");
                            return Task.CompletedTask;
                        });

        _unitOfWork.Setup(u => u.PeerDbRepository).Returns(_peerDbRepository.Object);
        _unitOfWork.Setup(u => u.GetPeersForStartupAsync()).ReturnsAsync(() => []);
        _peerDbRepository.Setup(r => r.AddOrUpdateAsync(It.IsAny<PeerModel>())).Returns(Task.CompletedTask);
        _serviceProvider.AddService(typeof(IUnitOfWork), _unitOfWork.Object);
        _channelMemory.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        _keyManager.Setup(k => k.GetNodePubKey()).Returns(s_ourNodeKey);
    }

    [Fact]
    public async Task Given_TwoRegisteredChannels_When_TheFirstsHandlerBlocks_Then_TheSecondChannelIsStillHandled()
    {
        // Arrange
        var (a, b) = (Channel(0xA1), Channel(0xB2));
        Register(a, b);
        await ConnectAsync();
        var blocked = Fee(a);
        var gate = Block(blocked);

        // Act
        Raise(blocked);
        await StartedAsync(blocked);
        var other = Fee(b);
        Raise(other);

        // Assert (channel b goes on while channel a's handler waits)
        await WaitForHandledAsync(other);
        Assert.DoesNotContain(blocked, Handled());
        gate.SetResult();
        await WaitForHandledAsync(blocked);
    }

    [Fact]
    public async Task Given_FiftyChannelsInterleaved_When_HandlersTakeRandomTime_Then_EachChannelKeepsItsArrivalOrder()
    {
        // Arrange (a property check against the sequential model: per channel, handled = sent, never two at once)
        var channels = Enumerable.Range(1, 50).Select(i => Channel((byte)i)).ToArray();
        Register(channels);
        var random = new Random(1357);
        _handlerDelayMs = () =>
        {
            lock (random)
                return random.Next(0, 3);
        };
        await ConnectAsync(new ChannelLaneOptions { Enabled = true, MaxConcurrentPerPeer = 8 });
        var sent = new Dictionary<ChannelId, List<UpdateFeeMessage>>();
        var all = new List<UpdateFeeMessage>();
        for (var i = 0; i < 1_000; i++)
        {
            var channel = channels[random.Next(channels.Length)];
            var message = Fee(channel);
            if (!sent.TryGetValue(channel, out var list))
                sent[channel] = list = [];
            list.Add(message);
            all.Add(message);
        }

        // Act
        foreach (var message in all)
            Raise(message);
        await WaitUntilAsync(() => Handled().Count == all.Count);

        // Assert
        var handled = Handled();
        foreach (var (channel, messages) in sent)
            Assert.Equal(messages, handled.Where(m => m.Payload.ChannelId == channel));
        Assert.Equal(0, _sameChannelOverlaps);
        Assert.True(_maxInFlight > 1, "different channels were never handled at the same time");
        Assert.True(_maxInFlight <= 8, $"{_maxInFlight} lanes ran at once with a per-peer cap of 8");
    }

    [Fact]
    public async Task Given_UnregisteredChannelIds_When_TheFirstsHandlerBlocks_Then_TheOtherWaitsOnThePeerLane()
    {
        // Arrange (temporary ids of an open, unknown ids: the serial peer lane, as the plain loop)
        await ConnectAsync();
        var first = Fee(Channel(0x01));
        var gate = Block(first);
        var second = Fee(Channel(0x02));

        // Act
        Raise(first);
        await StartedAsync(first);
        Raise(second);

        // Assert
        await AssertStaysAsync(() => Handled().Count == 0);
        gate.SetResult();
        await WaitUntilAsync(() => Handled().Count == 2);
        Assert.Equal([first, second], Handled());
    }

    [Fact]
    public async Task Given_ThePeerLaneIsBusy_When_ARegisteredChannelIsFirstSeen_Then_ItWaitsForThePeerLane()
    {
        // Arrange (the barrier: the peer lane's handler may be the one that registered the channel)
        var registered = Channel(0xC3);
        Register(registered);
        await ConnectAsync();
        var openFlow = Fee(Channel(0x7E));
        var gate = Block(openFlow);
        var steady = Fee(registered);

        // Act
        Raise(openFlow);
        await StartedAsync(openFlow);
        Raise(steady);

        // Assert
        await AssertStaysAsync(() => Handled().Count == 0);
        gate.SetResult();
        await WaitUntilAsync(() => Handled().Count == 2);
        Assert.Equal([openFlow, steady], Handled());
    }

    [Fact]
    public async Task Given_AChannelFirstSeenUnregistered_When_ItIsRegisteredLater_Then_ItStaysOnThePeerLane()
    {
        // Arrange (sticky assignment: a channel opened on this connection stays serial until the next one)
        var late = Channel(0x5A);
        await ConnectAsync();
        var early = Fee(late);
        Raise(early);
        await WaitForHandledAsync(early);
        Register(late);
        var openFlow = Fee(Channel(0x7E));
        var gate = Block(openFlow);

        // Act
        Raise(openFlow);
        await StartedAsync(openFlow);
        var after = Fee(late);
        Raise(after);

        // Assert (still behind the peer lane's busy item)
        await AssertStaysAsync(() => Handled().Count == 1);
        gate.SetResult();
        await WaitUntilAsync(() => Handled().Count == 3);
        Assert.Equal([early, openFlow, after], Handled());
    }

    [Fact]
    public async Task Given_APerPeerCapOfTwo_When_FiveChannelsBlock_Then_AtMostTwoHandlersRunAtOnce()
    {
        // Arrange
        var channels = Enumerable.Range(1, 5).Select(i => Channel((byte)(0x20 + i))).ToArray();
        Register(channels);
        await ConnectAsync(new ChannelLaneOptions { Enabled = true, MaxConcurrentPerPeer = 2 });
        var messages = channels.Select(Fee).ToArray();
        var gates = messages.Select(Block).ToArray();

        // Act
        foreach (var message in messages)
            Raise(message);
        await WaitUntilAsync(() => Volatile.Read(ref _inFlight) == 2);
        await AssertStaysAsync(() => Volatile.Read(ref _inFlight) == 2);
        foreach (var gate in gates)
            gate.TrySetResult();
        await WaitUntilAsync(() => Handled().Count == 5);

        // Assert
        Assert.Equal(2, _maxInFlight);
    }

    [Fact]
    public async Task Given_ANodeWideCapOfOne_When_TwoChannelsArrive_Then_TheyNeverOverlap()
    {
        // Arrange
        var (a, b) = (Channel(0xA1), Channel(0xB2));
        Register(a, b);
        await ConnectAsync(new ChannelLaneOptions { Enabled = true, MaxConcurrent = 1 });
        var first = Fee(a);
        var gate = Block(first);

        // Act
        Raise(first);
        await StartedAsync(first);
        var second = Fee(b);
        Raise(second);

        // Assert
        await AssertStaysAsync(() => Handled().Count == 0);
        gate.SetResult();
        await WaitUntilAsync(() => Handled().Count == 2);
        Assert.Equal(1, _maxInFlight);
    }

    [Fact]
    public async Task Given_AnErrorThatDisconnects_When_OtherLanesHaveQueuedWork_Then_ItIsDroppedAndTheRevertWaitsForTheBusyLane()
    {
        // Arrange
        var (a, b) = (Channel(0xA1), Channel(0xB2));
        Register(a, b);
        await ConnectAsync();
        var busy = Fee(b);
        var busyGate = Block(busy);
        var queued = Fee(b);
        var failing = Fee(a);
        _failures[failing] = new ChannelErrorException("bad", a, "bad");

        // Act
        Raise(busy);
        await StartedAsync(busy);
        Raise(queued);
        Raise(failing);
        var reason = await _disconnect.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await AssertStaysAsync(() => !Events().Contains("reverted"));
        busyGate.SetResult();
        await WaitUntilAsync(() => Events().Contains("reverted"));

        // Assert (the error went out for its channel; the queued message of the other lane was dropped)
        var error = Assert.IsType<ChannelErrorException>(reason);
        Assert.Equal(a, error.ChannelId);
        Assert.Contains(busy, Handled());
        Assert.DoesNotContain(queued, Handled());
    }

    [Fact]
    public async Task Given_AFailedChannel_When_OtherChannelsGoOn_Then_ItsErrorIsSentAndTheConnectionKept()
    {
        // Arrange
        var (a, b) = (Channel(0xA1), Channel(0xB2));
        Register(a, b);
        await ConnectAsync();
        var failing = Fee(a);
        _failures[failing] = new ChannelFailedException(a, "failed");
        var errorSent = new TaskCompletionSource<ErrorMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.Setup(p => p.SendErrorAsync(It.IsAny<ErrorMessage>()))
                   .Callback((ErrorMessage e) => errorSent.TrySetResult(e)).Returns(Task.CompletedTask);

        // Act
        Raise(failing);
        var error = await errorSent.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        var later = Fee(b);
        Raise(later);

        // Assert
        Assert.Equal(a, error.Payload.ChannelId);
        await WaitForHandledAsync(later);
        Assert.False(_disconnect.Task.IsCompleted);
    }

    [Fact]
    public async Task Given_RepliesOfTwoChannels_When_RaisedInTheirLanes_Then_EachChannelsRepliesKeepTheirOrder()
    {
        // Arrange (replies go to the lane's connection through the one outbox)
        var (a, b) = (Channel(0xA1), Channel(0xB2));
        Register(a, b);
        await ConnectAsync();
        var messages = new[] { Fee(a), Fee(b), Fee(a), Fee(b), Fee(a) };
        foreach (var message in messages)
            _replies[message] = new UpdateFeeMessage(new UpdateFeePayload(message.Payload.ChannelId,
                                                                          message.Payload.FeeratePerKw + 1));

        // Act
        foreach (var message in messages)
            Raise(message);
        await WaitUntilAsync(() => Sent().Count == messages.Length);

        // Assert
        var sent = Sent().Cast<UpdateFeeMessage>().ToList();
        foreach (var channel in new[] { a, b })
            Assert.Equal(messages.Where(m => m.Payload.ChannelId == channel).Select(m => m.Payload.FeeratePerKw + 1),
                         sent.Where(m => m.Payload.ChannelId == channel).Select(m => m.Payload.FeeratePerKw));
    }

    [Fact]
    public async Task Given_TheLanesAreBlocked_When_ThePeerSendsMoreThanTheQueuesHold_Then_TheReceivePathWaitsAndNothingIsLost()
    {
        // Arrange (one blocked channel lane: its queue fills, then the inbound queue, then the receive path waits)
        var a = Channel(0xA1);
        Register(a);
        await ConnectAsync();
        var first = Fee(a);
        var gate = Block(first);
        var rest = Enumerable.Range(0, 2_100).Select(_ => Fee(a)).ToArray();

        // Act
        Raise(first);
        await StartedAsync(first);
        var sending = Task.Run(() =>
        {
            foreach (var message in rest)
                Raise(message);
        }, TestContext.Current.CancellationToken);
        await AssertStaysAsync(() => !sending.IsCompleted);
        gate.SetResult();
        await sending.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Handled().Count == rest.Length + 1);

        // Assert
        Assert.Equal([first, .. rest], Handled());
    }

    [Fact]
    public async Task Given_AReconnection_When_TheOldConnectionsLaneIsBusy_Then_TheNewConnectionWaitsForItAndTheRevert()
    {
        // Arrange (plan R10: no message of the old connection after one of the new connection)
        var (a, b) = (Channel(0xA1), Channel(0xB2));
        Register(a, b);
        var second = new Mock<IPeerService>();
        SetupConnection(second);
        _connections.Enqueue(second);
        var peerManager = await ConnectAsync();
        var busy = Fee(a);
        var gate = Block(busy);
        Raise(busy);
        await StartedAsync(busy);

        // Act
        _connection.Raise(p => p.OnDisconnect += null, _connection.Object, new PeerDisconnectedEventArgs(_peerKey));
        await peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{_peerKey}@127.0.0.1:9735"));
        var onNewConnection = Fee(b);
        Raise(second, onNewConnection);
        await AssertStaysAsync(() => Handled().Count == 0);
        gate.SetResult();
        await WaitUntilAsync(() => Handled().Count == 2);

        // Assert
        Assert.Equal([busy, onNewConnection], Handled());
        Assert.Equal(["connected", "reverted", "connected"], Events());
    }

    [Fact]
    public async Task Given_TheReestablishPrologueIsRunning_When_MessagesArrive_Then_NoneIsHandledBeforeItEnds()
    {
        // Arrange (plan R2: our channel_reestablish for every channel before any message of the connection)
        var a = Channel(0xA1);
        Register(a);
        var prologue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _channelManager.Setup(cm => cm.OnPeerConnectedAsync(It.IsAny<CompactPubKey>()))
                       .Returns(async () =>
                        {
                            await prologue.Task;
                            Event("connected");
                            return (IReadOnlyList<ErrorMessage>)[];
                        });
        await ConnectAsync();
        var message = Fee(a);

        // Act
        Raise(message);
        await AssertStaysAsync(() => Handled().Count == 0);
        prologue.SetResult();

        // Assert
        await WaitForHandledAsync(message);
        Assert.Equal("connected", Events()[0]);
    }

    private async Task HandleAsync(UpdateFeeMessage message)
    {
        var channelId = message.Payload.ChannelId;
        if (_inFlightPerChannel.AddOrUpdate(channelId, 1, (_, c) => c + 1) != 1)
            Interlocked.Increment(ref _sameChannelOverlaps);
        var now = Interlocked.Increment(ref _inFlight);
        InterlockedMax(ref _maxInFlight, now);
        try
        {
            _started.GetOrAdd(message, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult();
            if (_gates.TryGetValue(message, out var gate))
                await gate.Task;
            if (_handlerDelayMs?.Invoke() is > 0 and var delay)
                await Task.Delay(delay);
            if (_replies.TryGetValue(message, out var reply))
                _channelManager.Raise(cm => cm.OnResponseMessageReady += null, _channelManager.Object,
                                      new ChannelResponseMessageEventArgs(_peerKey, reply));
            if (_failures.TryGetValue(message, out var failure))
                throw failure;
        }
        finally
        {
            lock (_handled)
                _handled.Add(message);
            Interlocked.Decrement(ref _inFlight);
            _inFlightPerChannel.AddOrUpdate(channelId, 0, (_, c) => c - 1);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value)
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
    }

    private void SetupConnection(Mock<IPeerService> connection)
    {
        connection.SetupGet(p => p.PeerPubKey).Returns(_peerKey);
        connection.SetupGet(p => p.Features).Returns(new FeatureOptions());
        connection.Setup(p => p.SendMessageAsync(It.IsAny<IChannelMessage>()))
                  .Callback((IChannelMessage m) =>
                   {
                       lock (_sent)
                           _sent.Add(m);
                   })
                  .Returns(Task.CompletedTask);
        connection.Setup(p => p.SendWarningAsync(It.IsAny<WarningException>())).Returns(Task.CompletedTask);
        connection.Setup(p => p.SendErrorAsync(It.IsAny<ErrorMessage>())).Returns(Task.CompletedTask);
        connection.Setup(p => p.Disconnect(It.IsAny<Exception?>()))
                  .Callback((Exception? e) => _disconnect.TrySetResult(e));
    }

    private async Task<PeerManager> ConnectAsync(ChannelLaneOptions? lanes = null)
    {
        var peerManager = new PeerManager(_channelManager.Object, _channelMemory.Object,
                                          new Mock<ILogger<PeerManager>>().Object, _peerServiceFactory.Object,
                                          _keyManager.Object, _tcpService.Object, _serviceProvider)
        {
            ChannelLanes = lanes ?? new ChannelLaneOptions { Enabled = true }
        };
        _tcpService.Setup(t => t.ConnectToPeerAsync(It.IsAny<PeerAddress>()))
                   .ReturnsAsync(new ConnectedPeer(_peerKey, "127.0.0.1", 9735, new Mock<TcpClient>().Object));
        await peerManager.ConnectToPeerAsync(new PeerAddressInfo($"{_peerKey}@127.0.0.1:9735"));
        return peerManager;
    }

    private static ChannelId Channel(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private void Register(params ChannelId[] channelIds) =>
        LaneTestChannels.Register(_channelMemory, _peerKey, channelIds);

    private UpdateFeeMessage Fee(ChannelId channelId) =>
        new(new UpdateFeePayload(channelId, Interlocked.Increment(ref _feerate)));

    private TaskCompletionSource Block(UpdateFeeMessage message)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates[message] = gate;
        return gate;
    }

    private Task StartedAsync(UpdateFeeMessage message) =>
        _started.GetOrAdd(message, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

    private void Raise(IChannelMessage message) => Raise(_connection, message);

    private void Raise(Mock<IPeerService> connection, IChannelMessage message) =>
        connection.Raise(p => p.OnChannelMessageReceived += null, connection.Object,
                         new ChannelMessageEventArgs(message, _peerKey));

    private void Event(string name)
    {
        lock (_events)
            _events.Add(name);
    }

    private List<string> Events()
    {
        lock (_events)
            return [.. _events];
    }

    private List<UpdateFeeMessage> Handled()
    {
        lock (_handled)
            return [.. _handled];
    }

    private List<IChannelMessage> Sent()
    {
        lock (_sent)
            return [.. _sent];
    }

    private Task WaitForHandledAsync(UpdateFeeMessage message) => WaitUntilAsync(() => Handled().Contains(message));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Asserts the condition holds for a while (nothing moves while a gate is closed).</summary>
    private static async Task AssertStaysAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + s_stableWindow;
        while (DateTime.UtcNow < until)
        {
            Assert.True(condition());
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}