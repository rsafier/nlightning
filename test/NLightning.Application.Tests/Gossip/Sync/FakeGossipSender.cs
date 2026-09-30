using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Relay.Interfaces;
using Domain.Gossip.Enums;
using Domain.Gossip.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// A fake <see cref="IGossipPeerSender"/> for the sync tests (NL-361): records every message handed to it and, unless
/// scripted otherwise, delivers it to the peer's service, so the <see cref="FakeGossipPeer"/> helpers keep working.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class FakeGossipSender : IGossipPeerSender
{
    private readonly Lock _lock = new();
    private readonly List<(GossipPeer Peer, IMessage Message)> _offers = [];
    private readonly Queue<GossipEnqueueResult> _scripted = new();

    /// <summary>Every message handed to <see cref="SendAsync"/>, refused ones included, in order.</summary>
    public IReadOnlyList<(GossipPeer Peer, IMessage Message)> Offers
    {
        get
        {
            lock (_lock)
                return _offers.ToList();
        }
    }

    /// <summary>How many messages were handed to <see cref="SendAsync"/> so far.</summary>
    public int OfferedCount
    {
        get
        {
            lock (_lock)
                return _offers.Count;
        }
    }

    /// <summary>Answers the next sends with <paramref name="result"/> instead of delivering (a full or gone
    /// outbox, NL-360); the offers are still recorded.</summary>
    public void ScriptNext(int count, GossipEnqueueResult result)
    {
        for (var i = 0; i < count; i++)
            _scripted.Enqueue(result);
    }

    public async ValueTask<GossipEnqueueResult> SendAsync(GossipPeer peer, IMessage message, int size)
    {
        lock (_lock)
            _offers.Add((peer, message));

        if (_scripted.TryDequeue(out var scripted))
            return scripted;

        await peer.Service.SendGossipMessageAsync(message);
        return GossipEnqueueResult.Queued;
    }

    public GossipOutboxDepth? GetDepth(GossipPeer peer) => null;
}