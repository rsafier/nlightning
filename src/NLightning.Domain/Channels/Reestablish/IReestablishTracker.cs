namespace NLightning.Domain.Channels.Reestablish;

using ValueObjects;

/// <summary>
/// Whether a channel was reestablished on the peer's current connection (BOLT 2: after a reconnection nothing but
/// <c>channel_reestablish</c> may be sent for a channel until the peer's <c>channel_reestablish</c> is processed).
/// </summary>
/// <remarks>
/// A channel that turned Open on the current connection counts as reestablished. Everything is forgotten when the
/// peer disconnects or reconnects. Not persisted: every restart starts with no channel reestablished. Read it for
/// <c>listchannels</c> (<c>ChannelInfoClientResponse.IsReestablished</c>).
/// </remarks>
public interface IReestablishTracker
{
    /// <summary>True when the channel can carry updates on the peer's current connection.</summary>
    bool IsReestablished(ChannelId channelId);
}