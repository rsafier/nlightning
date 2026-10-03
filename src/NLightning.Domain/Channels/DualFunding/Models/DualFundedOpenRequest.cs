namespace NLightning.Domain.Channels.DualFunding.Models;

using Accounting.Labels;
using Crypto.ValueObjects;
using LiquidityAds.Models;
using Money;

/// <summary>
/// A dual-funded (v2) open we initiate (<c>openchannel --dual-fund</c>, IPC key 5 of the open request; wave DF) for
/// <see cref="Interfaces.IDualFundedOpenService.OpenAsync"/>.
/// </summary>
/// <param name="PeerNodeId">The connected peer (it must have negotiated <c>option_dual_fund</c>).</param>
/// <param name="LocalFundingAmount">Our contribution to the funding output (<c>open_channel2.funding_satoshis</c>),
/// funded from the wallet through the interactive-tx contributor.</param>
/// <param name="FundingFeeratePerKw">The funding transaction's feerate, or null for the fee service's estimate.</param>
/// <param name="CommitmentFeeratePerKw">The first commitment's feerate, or null for the estimate.</param>
/// <param name="IsPublic">Announce the channel (<c>channel_flags</c> bit 0).</param>
/// <param name="RequireConfirmedInputs">Send <c>require_confirmed_inputs</c>.</param>
public sealed record DualFundedOpenRequest(
    CompactPubKey PeerNodeId,
    LightningMoney LocalFundingAmount,
    uint? FundingFeeratePerKw = null,
    uint? CommitmentFeeratePerKw = null,
    bool IsPublic = false,
    bool RequireConfirmedInputs = false)
{
    /// <summary>
    /// The operator's label and tags (NL-602 A3-T1) the channel row (and its <c>ChannelFunded</c> event) carries;
    /// <see cref="SourceLabels.None"/> for none.
    /// </summary>
    public SourceLabels Labels { get; init; } = SourceLabels.None;

    /// <summary>
    /// Inbound liquidity bought from the peer with this attempt (liquidity ads, NL-850); null buys none.
    /// </summary>
    public LiquidityRequest? Liquidity { get; init; }
}