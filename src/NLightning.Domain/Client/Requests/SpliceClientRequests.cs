namespace NLightning.Domain.Client.Requests;

using Channels.Splicing.Models;
using Channels.ValueObjects;
using LiquidityAds.Models;

/// <summary>
/// Splices wallet funds into a channel (<c>ClientCommand.SpliceIn</c>, splicing plan §3.10). DTO shell of the SP1
/// contracts; the handlers and the CLI are lane SP1-E's.
/// </summary>
public sealed class SpliceInClientRequest
{
    public SpliceInClientRequest(ChannelId channelId, ulong amountSat)
    {
        ChannelId = channelId;
        AmountSat = amountSat;
    }

    public ChannelId ChannelId { get; }

    /// <summary>The amount added to our channel balance, in satoshis.</summary>
    public ulong AmountSat { get; }

    /// <summary>The splice transaction's feerate, or null for the fee service's estimate.</summary>
    public uint? FeeRatePerKw { get; init; }

    /// <summary>
    /// Inbound liquidity to buy from the peer with the splice (liquidity ads, NL-771, <c>--request-inbound</c>), in
    /// satoshis, or null to buy none.
    /// </summary>
    public ulong? RequestInboundSat { get; init; }

    /// <summary>
    /// The most we pay for <see cref="RequestInboundSat"/>, in satoshis (<c>--max-liquidity-fee</c>); null for
    /// <c>Node:LiquidityAds:MaxFeeSat</c>.
    /// </summary>
    public ulong? MaxLiquidityFeeSat { get; init; }

    /// <summary>The <c>ISpliceService</c> request: a positive contribution, and the purchase when asked for.</summary>
    public SpliceRequest ToSpliceRequest() =>
        new(ChannelId, checked((long)AmountSat), FeeRatePerKw)
        {
            Liquidity = RequestInboundSat is { } inbound
                            ? new LiquidityRequest(inbound, null, MaxLiquidityFeeSat)
                            : null
        };
}

/// <summary>
/// Splices funds out of a channel (<c>ClientCommand.SpliceOut</c>, splicing plan §3.10). DTO shell of the SP1
/// contracts; the handlers and the CLI are lane SP1-E's.
/// </summary>
public sealed class SpliceOutClientRequest
{
    public SpliceOutClientRequest(ChannelId channelId, ulong amountSat)
    {
        ChannelId = channelId;
        AmountSat = amountSat;
    }

    public ChannelId ChannelId { get; }

    /// <summary>
    /// The amount paid to <see cref="Address"/> (or our wallet), in satoshis. Our channel balance drops by this plus our
    /// share of the splice fee.
    /// </summary>
    public ulong AmountSat { get; }

    /// <summary>Where the amount goes, or null for a new address of our wallet.</summary>
    public string? Address { get; init; }

    /// <summary>The splice transaction's feerate, or null for the fee service's estimate.</summary>
    public uint? FeeRatePerKw { get; init; }

    /// <summary>
    /// The <c>ISpliceService</c> request: -<see cref="AmountSat"/>, the operator's amount out. This is not the wire
    /// value: the service turns it into a <c>funding_contribution_satoshis</c> of -(amount + our fee share), because
    /// BOLT 2 sets the contribution to what leaves our balance and the new funding output to the previous capacity plus
    /// the contributions (splicing plan D16). Putting -amount on the wire makes the peer <c>tx_abort</c>.
    /// </summary>
    public SpliceRequest ToSpliceRequest() => new(ChannelId, -checked((long)AmountSat), FeeRatePerKw, Address);
}

/// <summary>
/// RBFs a channel's pending splice (<c>ClientCommand.BumpSplice</c>, splicing plan §3.10, wave SPR). DTO shell of the
/// SPR contracts; the handlers, the CLI and the auto-bump are lane SPR-B's. Answered with a
/// <c>SpliceClientResponse</c> (the new attempt's txid).
/// </summary>
public sealed class BumpSpliceClientRequest
{
    public BumpSpliceClientRequest(ChannelId channelId, uint feeRatePerKw)
    {
        ChannelId = channelId;
        FeeRatePerKw = feeRatePerKw;
    }

    public ChannelId ChannelId { get; }

    /// <summary>The new attempt's feerate, at least max(floor(25/24 x previous), previous + 25) sat/kw.</summary>
    public uint FeeRatePerKw { get; }

    /// <summary>The most our side may pay for the new attempt, in satoshis, or null for no cap.</summary>
    public ulong? MaxFeeSat { get; init; }

    /// <summary>The <c>ISpliceService</c> request: our contribution is kept from the latest attempt.</summary>
    public SpliceBumpRequest ToSpliceBumpRequest() => new(ChannelId, FeeRatePerKw, MaxFeeSat);
}