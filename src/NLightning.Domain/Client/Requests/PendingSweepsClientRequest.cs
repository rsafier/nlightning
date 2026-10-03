namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;

/// <summary>
/// Lists the on-chain resolution of closed channels (<c>ClientCommand.PendingSweeps</c>, BOLT 5 plan O3-T6).
/// </summary>
public sealed class PendingSweepsClientRequest
{
    /// <summary>Only this channel, when set.</summary>
    public ChannelId? ChannelId { get; init; }

    /// <summary>Also the channels already <c>Closed</c> (every output irrevocably resolved).</summary>
    public bool IncludeClosed { get; init; }
}