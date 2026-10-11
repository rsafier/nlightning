using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;

/// <summary>
/// Response for BumpOpen (ClientCommand 38, lane dfrbf): the new attempt's funding txid (hex, the usual display order).
/// </summary>
[MessagePackObject]
public sealed class BumpOpenIpcResponse
{
    [Key(0)] public required ChannelId ChannelId { get; init; }
    [Key(1)] public required string FundingTxId { get; init; }

    /// <summary>The liquidity bought with the new attempt (liquidity ads, NL-850), or null.</summary>
    [Key(2)] public LiquidityPurchaseIpcInfo? Purchase { get; init; }

    public static BumpOpenIpcResponse FromClientResponse(BumpOpenClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new BumpOpenIpcResponse
        {
            ChannelId = clientResponse.ChannelId,
            FundingTxId = Convert.ToHexString(((byte[])clientResponse.FundingTxId).Reverse().ToArray())
                                 .ToLowerInvariant(),
            Purchase = clientResponse.Purchase is { } purchase ? LiquidityPurchaseIpcInfo.From(purchase) : null
        };
    }
}