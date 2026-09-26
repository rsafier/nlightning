using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for ForceCloseChannel (ClientCommand 14): the channel's state, the broadcast status and our commitment's
/// txid (hex, the usual display order).
/// </summary>
[MessagePackObject]
public sealed class ForceCloseChannelIpcResponse
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required ChannelState State { get; init; }
    [Key(2)] public required string Status { get; init; }
    [Key(3)] public string? CommitmentTxId { get; init; }

    public static ForceCloseChannelIpcResponse FromClientResponse(ForceCloseChannelClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ForceCloseChannelIpcResponse
        {
            ChannelId = clientResponse.ChannelId,
            State = clientResponse.State,
            Status = clientResponse.Status,
            CommitmentTxId = clientResponse.CommitmentTxId is { } txId ? PendingSweepsIpcResponse.ToDisplay(txId) : null
        };
    }
}