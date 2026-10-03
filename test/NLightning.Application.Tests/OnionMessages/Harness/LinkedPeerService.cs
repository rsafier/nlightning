using System.Threading.Channels;

namespace NLightning.Application.Tests.OnionMessages.Harness;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.LiquidityAds.Models;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// One end of an in-process connection between two harness nodes: <see cref="SendOnionMessageAsync"/> hands the
/// message to the other node's service, as the peer's read loop would. Only onion messages flow. The link also holds
/// the connection's onion-message outbox (<see cref="TryEnqueueOnionMessage"/>): a bounded queue, like the
/// <c>PeerOutbox</c> onion-message class, pumped one message at a time into <see cref="SendOnionMessageAsync"/>.
/// </summary>
internal sealed class LinkedPeerService : IPeerService
{
    private readonly List<OnionMessageMessage> _sent = [];
    private readonly Channel<OnionMessageMessage> _outbox;
    private readonly Task _pump;
    private TaskCompletionSource _writable = CreateOpenGate();

    /// <param name="remote">The node at the other end.</param>
    /// <param name="features">What the connection negotiated.</param>
    /// <param name="outboxCapacity">The onion messages the outbox holds at most.</param>
    public LinkedPeerService(OnionMessageTestNode remote, FeatureOptions features, int outboxCapacity = 64)
    {
        Remote = remote;
        Features = features;
        _outbox = Channel.CreateBounded<OnionMessageMessage>(new BoundedChannelOptions(outboxCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// When true, the socket stops draining (a peer that stops reading): sends block until it is set back to false,
    /// and the outbox fills.
    /// </summary>
    public bool Stalled
    {
        get => !_writable.Task.IsCompleted;
        set
        {
            if (value == Stalled)
                return;
            if (value)
                _writable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            else
                _writable.TrySetResult();
        }
    }

    /// <summary>The node at the other end.</summary>
    public OnionMessageTestNode Remote { get; }

    /// <summary>The other end's view of this connection (the link it receives on).</summary>
    public LinkedPeerService? Reverse { get; set; }

    /// <summary>When set, the outbox refuses every message, as a full or closing one does.</summary>
    public bool RefuseSends { get; set; }

    /// <summary>
    /// The outbox side (what <c>PeerManager</c> does as <c>IPeerOnionMessageOutbox</c>): queues without blocking,
    /// false when full or refusing.
    /// </summary>
    public bool TryEnqueueOnionMessage(OnionMessageMessage message) =>
        !RefuseSends && _outbox.Writer.TryWrite(message);

    /// <summary>The messages sent over this link.</summary>
    public IReadOnlyList<OnionMessageMessage> Sent
    {
        get
        {
            lock (_sent)
                return _sent.ToList();
        }
    }

    public CompactPubKey PeerPubKey => Remote.NodeId;
    public FeatureOptions Features { get; }

    /// <summary>The same options as <see cref="Features"/>: the fake does not model the negotiation separately.</summary>
    public FeatureOptions PeerFeatures => Features;

    public DateTimeOffset? LastMessageReceivedAt => null;
    public AddressDescriptor? ObservedAddress => null;
    public WillFundRates? LiquidityRates => null;

#pragma warning disable CS0067 // events of the interface the harness never raises
    public event EventHandler<PeerDisconnectedEventArgs>? OnDisconnect;
    public event EventHandler<ChannelMessageEventArgs>? OnChannelMessageReceived;
    public event EventHandler<AttentionMessageEventArgs>? OnAttentionMessageReceived;
    public event EventHandler<Exception>? OnExceptionRaised;
    public event EventHandler<ChannelUpdateMessage>? OnChannelUpdateReceived;
#pragma warning restore CS0067

    public async Task SendOnionMessageAsync(OnionMessageMessage message, CancellationToken cancellationToken = default)
    {
        // A socket write that never completes while the reader is stalled
        await _writable.Task.WaitAsync(cancellationToken);
        lock (_sent)
            _sent.Add(message);
        Remote.Service.HandleIncoming(Reverse!, message);
    }

    public Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public Task WaitForInitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Disconnect(Exception? exception = null)
    {
    }

    public Task SendMessageAsync(IChannelMessage replyMessage) => throw new NotSupportedException();
    public Task SendGossipMessageAsync(IMessage message) => throw new NotSupportedException();
    public Task SendPeerStorageMessageAsync(IMessage message) => throw new NotSupportedException();
    public Task SendWarningAsync(WarningException we) => throw new NotSupportedException();
    public Task SendErrorAsync(ErrorMessage errorMessage) => throw new NotSupportedException();

    public void Dispose()
    {
        _outbox.Writer.TryComplete();
        _writable.TrySetResult();
        _pump.Wait(TimeSpan.FromSeconds(5));
    }

    private static TaskCompletionSource CreateOpenGate()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return gate;
    }

    private async Task PumpAsync()
    {
        await foreach (var message in _outbox.Reader.ReadAllAsync())
        {
            try
            {
                await SendOnionMessageAsync(message);
            }
            catch (ObjectDisposedException)
            {
                // The remote node is gone
                return;
            }
        }
    }
}