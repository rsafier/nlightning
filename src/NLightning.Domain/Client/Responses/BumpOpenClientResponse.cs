namespace NLightning.Domain.Client.Responses;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using LiquidityAds.Models;

/// <summary>
/// The outcome of <c>bumpopen</c> (<c>ClientCommand.BumpOpen</c>, lane dfrbf): the new attempt of the channel's
/// funding transaction, fully signed and published. Every signed attempt may still confirm; the channel follows the one
/// that does (NL-528).
/// </summary>
public sealed class BumpOpenClientResponse
{
    public BumpOpenClientResponse(ChannelId channelId, TxId fundingTxId)
    {
        ChannelId = channelId;
        FundingTxId = fundingTxId;
    }

    public ChannelId ChannelId { get; }

    /// <summary>The new attempt's funding transaction id (internal byte order).</summary>
    public TxId FundingTxId { get; }

    /// <summary>The liquidity bought with the new attempt (liquidity ads, NL-850), or null.</summary>
    public LiquidityPurchaseModel? Purchase { get; init; }
}