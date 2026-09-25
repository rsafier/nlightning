using NLightning.Domain.Protocol.Interfaces;

namespace NLightning.Application.Channels.Handlers.Interfaces;

using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;

/// <summary>
/// Base interface for all channel message handlers
/// </summary>
/// <typeparam name="TMessage">The type of message this handler can process</typeparam>
/// <remarks>
/// <c>ChannelManager</c> calls a handler while holding the channel's lock (<c>IChannelLockProvider</c>) and hands the
/// returned messages, in order, to the peer's outbox before releasing it. So wire order equals list order, and no
/// other transition of the same channel can put a message in between.
/// </remarks>
public interface IChannelMessageHandler<in TMessage> where TMessage : IChannelMessage
{
    /// <summary>
    /// Handles a channel message and returns the messages to send back to the same peer
    /// </summary>
    /// <param name="message">The message to handle</param>
    /// <param name="currentState">The current state of the channel</param>
    /// <param name="negotiatedFeatures">Features negotiated with the peer</param>
    /// <param name="peerPubKey">The public key of the peer</param>
    /// <returns>The replies in the order they must be sent (e.g. revoke_and_ack before commitment_signed); empty when
    /// there is nothing to send</returns>
    Task<IReadOnlyList<IChannelMessage>> HandleAsync(TMessage message, ChannelState currentState,
                                                     FeatureOptions negotiatedFeatures, CompactPubKey peerPubKey);
}