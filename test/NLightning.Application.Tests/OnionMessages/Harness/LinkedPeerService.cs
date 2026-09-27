namespace NLightning.Application.Tests.OnionMessages.Harness;

using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// One end of an in-process connection between two harness nodes: <see cref="SendOnionMessageAsync"/> hands the
/// message to the other node's service, as the peer's read loop would. Only onion messages flow.
/// </summary>
internal sealed class LinkedPeerService : IPeerService
{
    private readonly List<OnionMessageMessage> _sent = [];

    /// <param name="remote">The node at the other end.</param>
    /// <param name="features">What the connection negotiated.</param>
    public LinkedPeerService(OnionMessageTestNode remote, FeatureOptions features)
    {
        Remote = remote;
        Features = features;
    }

    /// <summary>The node at the other end.</summary>
    public OnionMessageTestNode Remote { get; }

    /// <summary>The other end's view of this connection (the link it receives on).</summary>
    public LinkedPeerService? Reverse { get; set; }

    /// <summary>When set, sends throw as a full outbox would.</summary>
    public bool RefuseSends { get; set; }

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
    public DateTimeOffset? LastMessageReceivedAt => null;
    public AddressDescriptor? ObservedAddress => null;

#pragma warning disable CS0067 // events of the interface the harness never raises
    public event EventHandler<PeerDisconnectedEventArgs>? OnDisconnect;
    public event EventHandler<ChannelMessageEventArgs>? OnChannelMessageReceived;
    public event EventHandler<AttentionMessageEventArgs>? OnAttentionMessageReceived;
    public event EventHandler<Exception>? OnExceptionRaised;
    public event EventHandler<ChannelUpdateMessage>? OnChannelUpdateReceived;
#pragma warning restore CS0067

    public Task SendOnionMessageAsync(OnionMessageMessage message, CancellationToken cancellationToken = default)
    {
        if (RefuseSends)
            throw new InvalidOperationException("Outbox full");

        lock (_sent)
            _sent.Add(message);
        Remote.Service.HandleIncoming(Reverse!, message);
        return Task.CompletedTask;
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
    }
}