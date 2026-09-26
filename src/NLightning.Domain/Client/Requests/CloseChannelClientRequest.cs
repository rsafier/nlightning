namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;

/// <summary>
/// Starts the cooperative close of a channel (<c>ClientCommand.CloseChannel</c>, BOLT2 plan N10-T3).
/// </summary>
public sealed class CloseChannelClientRequest
{
    public ChannelId ChannelId { get; }

    /// <summary>The feerate of our closing fee estimate, or null for the fee service's.</summary>
    public uint? FeeRatePerKw { get; init; }

    /// <summary>Negotiate without <c>fee_range</c> (the legacy "strictly between" rule).</summary>
    public bool NoFeeRange { get; init; }

    /// <summary>
    /// How long to wait for the closing transaction, in seconds, or null for the default (30). 0 returns as soon as
    /// our <c>shutdown</c> is out.
    /// </summary>
    public uint? WaitSeconds { get; init; }

    public CloseChannelClientRequest(ChannelId channelId)
    {
        ChannelId = channelId;
    }
}