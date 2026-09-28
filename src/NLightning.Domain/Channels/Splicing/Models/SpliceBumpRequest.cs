namespace NLightning.Domain.Channels.Splicing.Models;

using ValueObjects;

/// <summary>
/// An RBF of a channel's pending splice (<c>bumpsplice</c>, splicing plan §3.10, wave SPR) for
/// <see cref="Interfaces.ISpliceService.BumpAsync(SpliceBumpRequest, CancellationToken)"/>.
/// </summary>
/// <param name="ChannelId">The channel whose pending splice is bumped.</param>
/// <param name="FeeratePerKw">The new attempt's feerate (<c>tx_init_rbf.feerate</c>); at least
/// <c>InteractiveTxRbfRules.GetMinimumNextFeerate</c> of the latest attempt (IT-RBF-01).</param>
/// <param name="MaxFeeSatoshis">The most our side may pay for the new attempt (its fee share, D16), or null for no
/// cap; above it the bump is refused before anything is sent.</param>
/// <param name="ContributionSatoshis">Our new signed contribution (<c>funding_output_contribution</c>), or null to keep
/// the latest attempt's.</param>
public sealed record SpliceBumpRequest(
    ChannelId ChannelId,
    uint FeeratePerKw,
    ulong? MaxFeeSatoshis = null,
    long? ContributionSatoshis = null);