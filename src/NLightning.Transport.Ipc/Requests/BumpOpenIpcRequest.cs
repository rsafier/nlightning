using MessagePack;

namespace NLightning.Transport.Ipc.Requests;

using Domain.Channels.ValueObjects;
using Domain.Client.Requests;

/// <summary>
/// Request for BumpOpen (ClientCommand 38, lane dfrbf): RBF of our unconfirmed dual-funded open, answered with a
/// <c>BumpOpenIpcResponse</c>.
/// </summary>
[MessagePackObject]
public sealed class BumpOpenIpcRequest
{
    [Key(0)] public required ChannelId ChannelId { get; init; }

    /// <summary>The new attempt's feerate, in sat/kw.</summary>
    [Key(1)] public required uint FeeRatePerKw { get; init; }

    /// <summary>Our new contribution to the funding output, in satoshis, or null to keep it.</summary>
    [Key(2)] public ulong? ContributionSat { get; init; }

    /// <summary>
    /// Inbound liquidity to buy with the new attempt (liquidity ads, NL-771, <c>--request-inbound</c>), in satoshis,
    /// or null to repeat the previous attempt's purchase, if any; an older client sends none.
    /// </summary>
    [Key(3)] public ulong? RequestInboundSat { get; init; }

    /// <summary>The most we pay for the purchase, in satoshis (<c>--max-liquidity-fee</c>), or null.</summary>
    [Key(4)] public ulong? MaxLiquidityFeeSat { get; init; }

    public BumpOpenClientRequest ToClientRequest() =>
        new(ChannelId, FeeRatePerKw)
        {
            ContributionSat = ContributionSat,
            RequestInboundSat = RequestInboundSat,
            MaxLiquidityFeeSat = MaxLiquidityFeeSat
        };
}