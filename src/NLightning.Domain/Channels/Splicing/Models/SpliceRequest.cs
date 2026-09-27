namespace NLightning.Domain.Channels.Splicing.Models;

using ValueObjects;

/// <summary>
/// An operator's splice (<c>splicein</c>/<c>spliceout</c>, splicing plan §3.5 step 1, §3.10) for
/// <see cref="Interfaces.ISpliceService.StartAsync"/>.
/// </summary>
/// <param name="ChannelId">The channel to splice.</param>
/// <param name="ContributionSatoshis">Our signed contribution: positive adds wallet funds to our channel balance
/// (splice-in), negative takes that amount out of it (splice-out).</param>
/// <param name="FeeratePerKw">The splice transaction's feerate, or null for the fee service's estimate.</param>
/// <param name="SpliceOutAddress">Where a splice-out goes, or null for a new address of our wallet. Ignored for a
/// splice-in.</param>
public sealed record SpliceRequest(
    ChannelId ChannelId,
    long ContributionSatoshis,
    uint? FeeratePerKw = null,
    string? SpliceOutAddress = null);