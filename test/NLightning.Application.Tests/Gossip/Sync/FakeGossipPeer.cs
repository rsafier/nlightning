using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace NLightning.Application.Tests.Gossip.Sync;

using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Gossip.Addresses;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Graph;

/// <summary>
/// A connection for the sync tests: records every gossip message and warning we send, in order, and lets the test
/// wait for the next one.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class FakeGossipPeer : IPeerService
{
    private readonly Channel<IMessage> _sent = Channel.CreateUnbounded<IMessage>();
    private readonly List<IMessage> _all = [];
    private readonly Lock _lock = new();

    public FakeGossipPeer(byte seed, bool gossipQueries = true, bool gossipQueriesEx = false)
    {
        PeerPubKey = new TestGossipKey(seed).PubKey;
        Features = new FeatureOptions
        {
            ChainHashes = [ChainConstants.Regtest],
            GossipQueries = gossipQueries ? FeatureSupport.Optional : FeatureSupport.No,
            ExpandedGossipQueries = gossipQueriesEx ? FeatureSupport.Optional : FeatureSupport.No
        };
    }

    public CompactPubKey PeerPubKey { get; }
    public FeatureOptions Features { get; }
    public DateTimeOffset? LastMessageReceivedAt => null;
    public AddressDescriptor? ObservedAddress => null;
    public List<WarningException> Warnings { get; } = [];

    /// <summary>Everything sent so far (gossip and warnings, as <see cref="WarningMessage"/> stand-ins).</summary>
    public IReadOnlyList<IMessage> Sent
    {
        get
        {
            lock (_lock)
                return _all.ToList();
        }
    }

    public event EventHandler<PeerDisconnectedEventArgs>? OnDisconnect;
    public event EventHandler<ChannelMessageEventArgs>? OnChannelMessageReceived { add { } remove { } }
    public event EventHandler<AttentionMessageEventArgs>? OnAttentionMessageReceived { add { } remove { } }
    public event EventHandler<Exception>? OnExceptionRaised { add { } remove { } }
    public event EventHandler<ChannelUpdateMessage>? OnChannelUpdateReceived { add { } remove { } }

    public Task SendGossipMessageAsync(IMessage message)
    {
        Record(message);
        return Task.CompletedTask;
    }

    public Task SendWarningAsync(WarningException we)
    {
        lock (_lock)
            Warnings.Add(we);
        Record(new WarningMessage(new Domain.Protocol.Payloads.ErrorPayload(
                                      System.Text.Encoding.UTF8.GetBytes(we.Message))));
        return Task.CompletedTask;
    }

    /// <summary>The next message we send, or a failure after <paramref name="timeout"/> (default 5 s).</summary>
    public async Task<T> NextAsync<T>(TimeSpan? timeout = null) where T : IMessage
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
        var message = await _sent.Reader.ReadAsync(cts.Token);
        return Assert.IsType<T>(message);
    }

    /// <summary>True when nothing more is sent within <paramref name="wait"/>.</summary>
    public async Task<bool> NothingSentWithinAsync(TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try
        {
            await _sent.Reader.WaitToReadAsync(cts.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    public void Disconnect(Exception? exception = null) =>
        OnDisconnect?.Invoke(this, new PeerDisconnectedEventArgs(PeerPubKey, exception));

    public Task WaitForInitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(true);
    public Task SendMessageAsync(IChannelMessage replyMessage) => Task.CompletedTask;
    public Task SendErrorAsync(ErrorMessage errorMessage) => Task.CompletedTask;
    public void Dispose() { }

    private void Record(IMessage message)
    {
        lock (_lock)
            _all.Add(message);
        _sent.Writer.TryWrite(message);
    }
}