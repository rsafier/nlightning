namespace NLightning.Domain.Channels.DualFunding.Models;

using Crypto.ValueObjects;
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
    bool RequireConfirmedInputs = false);