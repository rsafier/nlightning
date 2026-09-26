using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Requests;

/// <summary>
/// Request for CloseChannel (ClientCommand 13).
/// </summary>
[MessagePackObject]
public sealed class CloseChannelIpcRequest
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>The feerate of our closing fee estimate, or null for the daemon's.</summary>
    [Key(1)] public uint? FeeRatePerKw { get; init; }

    /// <summary>Negotiate without <c>fee_range</c>.</summary>
    [Key(2)] public bool NoFeeRange { get; init; }

    /// <summary>How long the daemon waits for the closing transaction, in seconds, or null for its default.</summary>
    [Key(3)] public uint? WaitSeconds { get; init; }

    public CloseChannelClientRequest ToClientRequest()
    {
        return new CloseChannelClientRequest(ChannelId)
        {
            FeeRatePerKw = FeeRatePerKw,
            NoFeeRange = NoFeeRange,
            WaitSeconds = WaitSeconds
        };
    }
}