namespace NLightning.Application.Gossip.Events;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;

/// <summary>
/// A <c>channel_update</c> of ours for the channel peer <see cref="PeerPubKey"/>.
/// </summary>
public sealed class ChannelUpdateReadyEventArgs(CompactPubKey peerPubKey, ChannelUpdateMessage message) : EventArgs
{
    public CompactPubKey PeerPubKey { get; } = peerPubKey;
    public ChannelUpdateMessage Message { get; } = message;
}