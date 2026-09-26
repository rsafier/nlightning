namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// A channel has HTLC rows in a legacy state (0-3), written before the commitment state machine existed. They cannot be
/// mapped to it, so the channel is refused instead of guessed (NL-025).
/// </summary>
public sealed class LegacyHtlcStateException : InvalidOperationException
{
    public ChannelId ChannelId { get; }

    public LegacyHtlcStateException(ChannelId channelId, ulong htlcId, byte state)
        : base($"Channel {channelId} has HTLC {htlcId} in legacy state {state}, written before the commitment state "
             + "machine existed (NL-025); it cannot be restored")
    {
        ChannelId = channelId;
    }
}