using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Gossip.Sync;

using Application.Gossip.Relay.Interfaces;
using Domain.Gossip.Enums;
using Domain.Gossip.Models;
using Domain.Protocol.Interfaces;

/// <summary>
/// A fake <see cref="IGossipPeerSender"/> for the sync tests (NL-361): records every message handed to it and, unless
/// scripted otherwise, delivers it to the peer's service, so the <see cref="FakeGossipPeer"/> helpers keep working.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class FakeGossipSender : IGossipPeerSender
{
    private readonly Lock _lock = new();
    private readonly List<(GossipPeer Peer, IMessage Message)> _offers = [];
    private readonly List<(GossipEnqueueResult Result, int Count)> _scripted = [];

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
        lock (_lock)
            _scripted.Add((result, count));
    }

    public async ValueTask<GossipEnqueueResult> SendAsync(GossipPeer peer, IMessage message, int size)
    {
        _ = size; // the sync sends without a known wire size (the outbox counts it by message only)
        GossipEnqueueResult? scripted = null;
        lock (_lock)
        {
            _offers.Add((peer, message));
            if (_scripted.Count > 0)
            {
                var (result, count) = _scripted[0];
                scripted = result;
                if (count > 1)
                    _scripted[0] = (result, count - 1);
                else
                    _scripted.RemoveAt(0);
            }
        }

        if (scripted is { } refused)
            return refused;

        await peer.Service.SendGossipMessageAsync(message);
        return GossipEnqueueResult.Queued;
    }

    public GossipOutboxDepth? GetDepth(GossipPeer peer) => null;
}