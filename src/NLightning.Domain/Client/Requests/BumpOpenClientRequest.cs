namespace NLightning.Domain.Client.Requests;

using Channels.ValueObjects;

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
}