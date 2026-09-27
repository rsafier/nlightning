using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for SpliceIn and SpliceOut (ClientCommand 33, 34): the splice txid (hex, the usual display order), the new
/// capacity and the negotiation's state. DTO shell of the SP1 contracts (lane SP1-E).
/// </summary>
[MessagePackObject]
public sealed class SpliceIpcResponse
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required SpliceNegotiationState State { get; init; }
    [Key(2)] public string? SpliceTxId { get; init; }
    [Key(3)] public ulong? NewCapacitySat { get; init; }
    [Key(4)] public string? FailureReason { get; init; }

    public static SpliceIpcResponse FromClientResponse(SpliceClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new SpliceIpcResponse
        {
            ChannelId = clientResponse.ChannelId,
            State = clientResponse.State,
            SpliceTxId = clientResponse.SpliceTxId is { } txId
                             ? Convert.ToHexString(((byte[])txId).Reverse().ToArray()).ToLowerInvariant()
                             : null,
            NewCapacitySat = clientResponse.NewCapacitySat,
            FailureReason = clientResponse.FailureReason
        };
    }
}