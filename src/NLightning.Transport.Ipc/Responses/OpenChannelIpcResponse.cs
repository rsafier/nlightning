using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for OpenChannel command
/// </summary>
[MessagePackObject]
public sealed class OpenChannelIpcResponse
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>The published funding transaction of a dual-funded open (NL-535); null for a v1 open.</summary>
    [Key(1)] public TxId? FundingTxId { get; init; }

    /// <summary>The funding output's index in <see cref="FundingTxId"/>, or null.</summary>
    [Key(2)] public uint? FundingOutputIndex { get; init; }

    /// <summary>The liquidity bought with a dual-funded open (liquidity ads, NL-850), or null.</summary>
    [Key(3)] public LiquidityPurchaseIpcInfo? Purchase { get; init; }

    public static OpenChannelIpcResponse FromClientResponse(OpenChannelClientResponse clientResponse)
    {
        return new OpenChannelIpcResponse
        {
            ChannelId = clientResponse.ChannelId,
            FundingTxId = clientResponse.FundingTxId,
            FundingOutputIndex = clientResponse.FundingOutputIndex,
            Purchase = clientResponse.Purchase is { } purchase ? LiquidityPurchaseIpcInfo.From(purchase) : null
        };
    }
}