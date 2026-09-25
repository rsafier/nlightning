using System.Collections.Concurrent;

namespace NLightning.Integration.Tests.Docker.Abcd;

using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Utils;

/// <summary>
/// Records every channel message a node sends. All of them go through
/// <see cref="IChannelManager.OnResponseMessageReady"/> (replies, <c>channel_reestablish</c> on a new connection,
/// retransmissions, the send side), which is the node's single ordered send path.
/// </summary>
/// <remarks>
/// A restart builds a new service graph, so call <see cref="Attach"/> again after every <c>StartAsync</c>; what the
/// node sends between the start and the attach is not recorded. Received messages are not hooked (the peer services
/// are created per connection, too late to see the first message); a channel's <c>IsReestablished</c> flag is the
/// receive-side evidence.
/// </remarks>
public sealed class ChannelMessageRecorder : IDisposable
{
    private readonly ConcurrentQueue<SentMessage> _sent = new();
    private IChannelManager? _channelManager;
    private long _sequence;

    public string NodeName { get; }

    public ChannelMessageRecorder(string nodeName)
    {
        NodeName = nodeName;
    }

    /// <summary>
    /// The sequence number of the last recorded message; pass it to <see cref="CountSent{TMessage}"/> to count only
    /// what was sent afterwards.
    /// </summary>
    public long Mark => Interlocked.Read(ref _sequence);

    /// <summary>
    /// Starts recording what the running <paramref name="node"/> sends (and stops recording the previous graph).
    /// </summary>
    public void Attach(NLightningTestNode node)
    {
        Detach();
        _channelManager = node.ChannelManager;
        _channelManager.OnResponseMessageReady += OnSent;
    }

    public void Detach()
    {
        var channelManager = Interlocked.Exchange(ref _channelManager, null);
        if (channelManager is not null)
            channelManager.OnResponseMessageReady -= OnSent;
    }

    /// <summary>
    /// How many <typeparamref name="TMessage"/> for <paramref name="channelId"/> were sent after
    /// <paramref name="afterMark"/> (to <paramref name="peer"/> when given).
    /// </summary>
    public int CountSent<TMessage>(ChannelId channelId, long afterMark = 0, CompactPubKey? peer = null)
        where TMessage : IChannelMessage =>
        _sent.Count(m => m.Sequence > afterMark
                      && m.Message is TMessage
                      && m.Message.Payload.ChannelId == channelId
                      && (peer is null || m.Peer.Equals(peer.Value)));

    public void Dispose() => Detach();

    private void OnSent(object? _, ChannelResponseMessageEventArgs args)
    {
        // Raised under the channel's lock: only enqueue
        _sent.Enqueue(new SentMessage(Interlocked.Increment(ref _sequence), args.PeerPubKey, args.ResponseMessage));
    }

    private sealed record SentMessage(long Sequence, CompactPubKey Peer, IChannelMessage Message);
}