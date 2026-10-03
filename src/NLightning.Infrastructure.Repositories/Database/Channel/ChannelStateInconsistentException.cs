namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// A channel's stored commitment state does not fit together (the engine's restore refused it). A channel load reads
/// its rows in several queries without a read transaction, so a save committing between them gives the same error
/// once; <see cref="ChannelDbRepository"/> reads the channel again before it gives up (NL-805).
/// </summary>
public sealed class ChannelStateInconsistentException : InvalidOperationException
{
    public ChannelId ChannelId { get; }

    public ChannelStateInconsistentException(ChannelId channelId, string message, Exception innerException)
        : base($"The stored commitment state of channel {channelId} is inconsistent: {message}", innerException)
    {
        ChannelId = channelId;
    }
}