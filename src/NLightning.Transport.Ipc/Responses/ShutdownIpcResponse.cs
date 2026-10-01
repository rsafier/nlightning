using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Enums;
using Domain.Client.Responses;

/// <summary>
/// Response for Shutdown (ClientCommand 39, NL-591/NL-592): how the shutdown ended, with the counts. A refusal (HTLCs
/// in flight without <c>--force</c>/<c>--wait</c>, or a shutdown already running) is an error envelope instead, as in
/// the first pass. Keys are append-only: an older client reads only <see cref="ChannelCount"/>.
/// </summary>
[MessagePackObject]
public sealed class ShutdownIpcResponse
{
    /// <summary>The channels that are not closed; they reestablish when the node starts again.</summary>
    [Key(0)] public int ChannelCount { get; init; }

    /// <summary>How the shutdown ended (NL-592).</summary>
    [Key(1)] public ShutdownOutcome Outcome { get; init; }

    /// <summary>The HTLCs in flight when the node stopped (<see cref="ShutdownOutcome.Forced"/>) or when the wait
    /// timed out (<see cref="ShutdownOutcome.TimedOut"/>).</summary>
    [Key(2)] public int HtlcsInFlight { get; init; }

    /// <summary>The negotiations still running at the same moments (NL-592).</summary>
    [Key(3)] public int NegotiationCount { get; init; }

    /// <summary>The channels the busy counts come from (NL-592).</summary>
    [Key(4)] public required List<ShutdownBusyChannelIpc> BusyChannels { get; init; }

    /// <summary>The nearest cltv_expiry among the in-flight HTLCs; 0 when none was in flight (NL-592).</summary>
    [Key(5)] public uint NearestCltvExpiry { get; init; }

    /// <summary>The blocks that remain until our deadline to act on it; -1 when the height is unknown (NL-592).</summary>
    [Key(6)] public int BlocksUntilDeadline { get; init; }

    public static ShutdownIpcResponse FromClientResponse(ShutdownClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ShutdownIpcResponse
        {
            ChannelCount = clientResponse.ChannelCount,
            Outcome = clientResponse.Outcome,
            HtlcsInFlight = clientResponse.HtlcsInFlight,
            NegotiationCount = clientResponse.NegotiationCount,
            BusyChannels = clientResponse.BusyChannels.Select(ShutdownBusyChannelIpc.From).ToList(),
            NearestCltvExpiry = clientResponse.NearestCltvExpiry,
            BlocksUntilDeadline = clientResponse.BlocksUntilDeadline
        };
    }
}

/// <summary>One busy channel over the wire (NL-592).</summary>
[MessagePackObject]
public sealed class ShutdownBusyChannelIpc
{
    /// <summary>The channel id, as <c>listchannels</c> prints it.</summary>
    [Key(0)] public required string ChannelId { get; init; }

    /// <summary>Its HTLCs in flight.</summary>
    [Key(1)] public int HtlcsInFlight { get; init; }

    /// <summary>Whether a negotiation runs on it, it is quiescent for one, or its open is not signed yet.</summary>
    [Key(2)] public bool Negotiating { get; init; }

    public static ShutdownBusyChannelIpc From(ShutdownBusyChannel channel) =>
        new()
        {
            ChannelId = channel.ChannelId,
            HtlcsInFlight = channel.HtlcsInFlight,
            Negotiating = channel.Negotiating
        };

    public ShutdownBusyChannel ToClient() => new(ChannelId, HtlcsInFlight, Negotiating);
}