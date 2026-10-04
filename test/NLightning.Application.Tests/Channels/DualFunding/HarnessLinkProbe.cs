using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>The send side's link: up once the channel manager marked it (channel opened or reestablished).</summary>
[ExcludeFromCodeCoverage]
internal sealed class HarnessLinkProbe : IPeerLivenessProbe
{
    private readonly ConcurrentDictionary<ChannelId, byte> _links = new();

    public Task<bool> IsAliveAsync(ChannelId channelId, CompactPubKey peerPubKey,
                                   CancellationToken cancellationToken = default) =>
        Task.FromResult(_links.ContainsKey(channelId));

    public void MarkLinkUp(ChannelId channelId, CompactPubKey peerPubKey)
    {
        _links[channelId] = 0;
        LinkUp?.Invoke(this, new ChannelLinkUpEventArgs(channelId, peerPubKey));
    }

    public event EventHandler<ChannelLinkUpEventArgs>? LinkUp;

    public void Clear() => _links.Clear();
}