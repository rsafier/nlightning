namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using LiquidityAds.Models;

public sealed class OpenChannelClientResponse
{
    public ChannelId ChannelId { get; }

    /// <summary>
    /// The funding transaction when it is signed and published by the time the open returns: a dual-funded open
    /// (NL-535), whose first attempt an operator may bump with <c>bumpopen</c> before it confirms. Null for a v1 open,
    /// whose funding the open subscription reports once the peer's <c>funding_signed</c> arrived.
    /// </summary>
    public TxId? FundingTxId { get; init; }

    /// <summary>The funding output's index in <see cref="FundingTxId"/>, or null.</summary>
    public uint? FundingOutputIndex { get; init; }

    /// <summary>The liquidity bought with a dual-funded open (liquidity ads, NL-771), or null.</summary>
    public LiquidityPurchaseModel? Purchase { get; init; }

    public OpenChannelClientResponse(ChannelId channelId)
    {
        ChannelId = channelId;
    }
}