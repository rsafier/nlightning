using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for SpliceIn, SpliceOut and BumpSplice (ClientCommand 33, 34, 37): the splice txid (hex, the usual
/// display order), the new capacity and the negotiation's state. DTO shell of the SP1 contracts (lane SP1-E).
/// </summary>
[MessagePackObject]
public sealed class SpliceIpcResponse
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required SpliceNegotiationState State { get; init; }
    [Key(2)] public string? SpliceTxId { get; init; }
    [Key(3)] public ulong? NewCapacitySat { get; init; }
    [Key(4)] public string? FailureReason { get; init; }

    /// <summary>
    /// Information that is not a failure (a splice stopped at CommitmentSigned that completes on the reconnection), or
    /// null.
    /// </summary>
    [Key(5)] public string? Note { get; init; }

    /// <summary>The liquidity bought with the splice (liquidity ads, NL-771), or null.</summary>
    [Key(6)] public LiquidityPurchaseIpcInfo? Purchase { get; init; }

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
            FailureReason = clientResponse.FailureReason,
            Note = clientResponse.Note,
            Purchase = clientResponse.Purchase is { } purchase ? LiquidityPurchaseIpcInfo.From(purchase) : null
        };
    }
}