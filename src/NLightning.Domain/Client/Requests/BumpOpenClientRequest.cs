namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;
using LiquidityAds.Models;

/// <summary>
/// RBFs our unconfirmed dual-funded open (<c>ClientCommand.BumpOpen</c>, BOLT 2 "Fee bumping", lane dfrbf): a new
/// attempt of the funding transaction at a higher feerate, optionally with another contribution of ours. Answered with a
/// <c>BumpOpenClientResponse</c>.
/// </summary>
public sealed class BumpOpenClientRequest
{
    public BumpOpenClientRequest(ChannelId channelId, uint feeRatePerKw)
    {
        ChannelId = channelId;
        FeeRatePerKw = feeRatePerKw;
    }

    public ChannelId ChannelId { get; }

    /// <summary>The new attempt's feerate, at least max(floor(25/24 x previous), previous + 25) sat/kw.</summary>
    public uint FeeRatePerKw { get; }

    /// <summary>
    /// Our new contribution to the funding output, in satoshis, paid from the inputs of our last attempt; null keeps
    /// the current one (BOLT 2: the opener "MAY set <c>funding_output_contribution</c> to a different value").
    /// </summary>
    public ulong? ContributionSat { get; init; }

    /// <summary>
    /// Inbound liquidity to buy with the new attempt (liquidity ads, NL-771, <c>--request-inbound</c>), in satoshis;
    /// null repeats the purchase of the attempt it replaces, if any (BOLT PR #1153: an RBF after a purchase requests
    /// funding again).
    /// </summary>
    public ulong? RequestInboundSat { get; init; }

    /// <summary>
    /// The most we pay for the purchase, in satoshis (<c>--max-liquidity-fee</c>); null for
    /// <c>Node:LiquidityAds:MaxFeeSat</c>. Only with <see cref="RequestInboundSat"/>.
    /// </summary>
    public ulong? MaxLiquidityFeeSat { get; init; }

    /// <summary>The purchase this attempt asks for, or null to repeat the previous attempt's (if any).</summary>
    public LiquidityRequest? ToLiquidityRequest() =>
        RequestInboundSat is { } inbound ? new LiquidityRequest(inbound, null, MaxLiquidityFeeSat) : null;
}