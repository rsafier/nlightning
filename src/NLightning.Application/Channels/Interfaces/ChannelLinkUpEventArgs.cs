namespace NLightning.Application.Channels.Interfaces;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// A channel whose link was marked up on its peer's current connection (<see cref="IPeerLivenessProbe.MarkLinkUp"/>
/// found the peer's connection, NL-364).
/// </summary>
public sealed class ChannelLinkUpEventArgs(ChannelId channelId, CompactPubKey peerPubKey) : EventArgs
{
    public ChannelId ChannelId { get; } = channelId;

    public CompactPubKey PeerPubKey { get; } = peerPubKey;
}