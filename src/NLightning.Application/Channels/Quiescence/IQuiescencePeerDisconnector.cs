namespace NLightning.Application.Channels.Quiescence;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Closes the connection of a channel whose quiescence lasted too long (BOLT 2 Q-R-03, <c>Node:Quiescence</c>).
/// </summary>
/// <remarks>
/// The default, <see cref="PeerServiceQuiescenceDisconnector"/>, sends a <c>warning</c> for the channel and closes the
/// peer's current connection without suppressing our reconnect loop: the reconnection ends the quiescence (Q-R-04) and
/// the channel works again after its <c>channel_reestablish</c>.
/// </remarks>
public interface IQuiescencePeerDisconnector
{
    /// <summary>Closes the connection to <paramref name="peerPubKey"/>; never throws.</summary>
    /// <param name="peerPubKey">The channel's peer.</param>
    /// <param name="channelId">The channel whose quiescence timed out (the <c>warning</c> names it).</param>
    /// <param name="reason">The text of the <c>warning</c>.</param>
    void Disconnect(CompactPubKey peerPubKey, ChannelId channelId, string reason);
}