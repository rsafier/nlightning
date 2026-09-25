namespace NLightning.Domain.Channels.Interfaces;

using Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Events;
using Models;
using Node.Options;

public interface IChannelManager
{
    /// <summary>
    /// Raised for every message a channel transition wants to send, including the replies to
    /// <see cref="HandleChannelMessageAsync"/>, in wire order.
    /// </summary>
    /// <remarks>
    /// Raised synchronously while the channel's lock is held, so a subscriber must only enqueue (never block or send
    /// inline): that is what keeps wire order equal to persist order (BOLT2 plan D2).
    /// </remarks>
    event EventHandler<ChannelResponseMessageEventArgs> OnResponseMessageReady;

    /// <summary>
    /// Processes one channel message from <paramref name="peerPubKey"/> under the channel's lock. The replies are
    /// raised through <see cref="OnResponseMessageReady"/>, in order, before the lock is released and before this task
    /// completes. Failures are thrown as channel-scoped <c>ChannelErrorException</c>/<c>ChannelWarningException</c>.
    /// </summary>
    /// <returns>The replies that were raised (for callers and tests; they are already on their way).</returns>
    Task<IReadOnlyList<IChannelMessage>> HandleChannelMessageAsync(IChannelMessage message,
                                                                   FeatureOptions negotiatedFeatures,
                                                                   CompactPubKey peerPubKey);

    Task RegisterExistingChannelAsync(ChannelModel channel);

    /// <summary>
    /// Starts opening a channel we fund: under the temporary channel's lock, stores <paramref name="channel"/> as a
    /// temporary channel of <paramref name="peerPubKey"/> and raises <paramref name="openChannelMessage"/> through
    /// <see cref="OnResponseMessageReady"/>, so it goes out through the peer's ordered send path like every other
    /// channel message (BOLT2 plan §3.7).
    /// </summary>
    Task StartOpeningChannelAsync(CompactPubKey peerPubKey, ChannelModel channel, IChannelMessage openChannelMessage);
}