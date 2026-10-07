namespace NLightning.Domain.Node.Events;

using Crypto.ValueObjects;

/// <summary>A fully initialized peer session became online or the current session ended.</summary>
public sealed class PeerStateChangedEventArgs(CompactPubKey peerPubKey, bool online) : EventArgs
{
    public CompactPubKey PeerPubKey { get; } = peerPubKey;
    public bool Online { get; } = online;
}