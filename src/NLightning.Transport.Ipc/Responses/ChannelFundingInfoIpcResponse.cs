using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Splicing.Enums;
using Domain.Client.Responses;
using Domain.Money;

/// <summary>
/// One funding of a channel in a <see cref="ChannelInfoIpcResponse"/> (splicing plan §3.10 <c>fundings[]</c>, SP2-0;
/// lane SP2-D).
/// </summary>
[MessagePackObject]
public sealed class ChannelFundingInfoIpcResponse
{
    [Key(0)] public required TxId FundingTxId { get; init; }
    [Key(1)] public ushort OutputIndex { get; init; }
    [Key(2)] public required LightningMoney Capacity { get; init; }
    [Key(3)] public ChannelFundingStatus Status { get; init; }
    [Key(4)] public ChannelFundingKind Kind { get; init; }
    [Key(5)] public uint? Depth { get; init; }

    /// <summary>The short channel id as a BOLT 7 uint64, once confirmed.</summary>
    [Key(6)] public ulong? ShortChannelId { get; init; }

    [Key(7)] public bool SpliceLockedSent { get; init; }
    [Key(8)] public bool SpliceLockedReceived { get; init; }

    public static ChannelFundingInfoIpcResponse FromClientResponse(ChannelFundingInfoClientResponse funding)
    {
        ArgumentNullException.ThrowIfNull(funding);
        return new ChannelFundingInfoIpcResponse
        {
            FundingTxId = funding.FundingTxId,
            OutputIndex = funding.OutputIndex,
            Capacity = funding.Capacity,
            Status = funding.Status,
            Kind = funding.Kind,
            Depth = funding.Depth,
            ShortChannelId = funding.ShortChannelId is { } scid ? RetiredScidInfoIpcResponse.ToUInt64(scid) : null,
            SpliceLockedSent = funding.SpliceLockedSent,
            SpliceLockedReceived = funding.SpliceLockedReceived
        };
    }
}