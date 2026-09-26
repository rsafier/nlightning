namespace NLightning.Domain.Gossip.Interfaces;

using Protocol.Interfaces;

/// <summary>
/// Takes the gossip our node generates (plan BOLT7 §3.2 step 5): the assembled <c>channel_announcement</c> of one of
/// our public channels, our <c>channel_update</c>s for it and our <c>node_announcement</c>. They go into the graph
/// without a chain lookup (our own channels need none) and without the ingress queue.
/// </summary>
public interface IOwnGossipSink
{
    /// <summary>
    /// Applies <paramref name="message"/> (a <c>ChannelAnnouncementMessage</c>, <c>ChannelUpdateMessage</c> or
    /// <c>NodeAnnouncementMessage</c>) to the graph. A <c>node_announcement</c> is persisted before the task completes
    /// (its timestamp must increase across restarts).
    /// </summary>
    /// <exception cref="ArgumentException">The message is not one of the three.</exception>
    Task SubmitOwnAsync(IMessage message, CancellationToken cancellationToken = default);
}