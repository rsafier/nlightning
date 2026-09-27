namespace NLightning.Application.Channels.Backup.Models;

using Domain.Bitcoin.Events;

/// <summary>What <see cref="Interfaces.IFundingSpendLocator"/> found for one funding output.</summary>
public enum FundingSpendStatus
{
    /// <summary>The funding output is confirmed and unspent: the chain monitor sees its spend.</summary>
    Unspent,

    /// <summary>
    /// The funding is not known to be confirmed: the backup has neither a short channel id nor a funding height, or the
    /// funding transaction is in no block from its funding height to the tip (it never confirmed).
    /// </summary>
    NotConfirmed,

    /// <summary>The output is spent and the spending transaction was found (<see cref="FundingSpendLocation.Spend"/>).</summary>
    SpentFound,

    /// <summary>The output is spent but its spend is not in the searched blocks: the blocks from
    /// <see cref="FundingSpendLocation.SearchedFromHeight"/> - 1 down to <see cref="FundingSpendLocation.FloorHeight"/>
    /// are left to search (<see cref="Interfaces.IFundingSpendLocator.RescanAsync"/>; none when the two are equal).
    /// </summary>
    SpentNotFound,

    /// <summary>The chain could not be read (<see cref="FundingSpendLocation.Error"/>).</summary>
    ChainUnavailable
}

/// <summary>The outcome of <see cref="Interfaces.IFundingSpendLocator.LocateAsync"/>.</summary>
/// <param name="Status">What was found.</param>
/// <param name="Spend">The spend, as the chain monitor would have reported it, for
/// <see cref="FundingSpendStatus.SpentFound"/>.</param>
/// <param name="SearchedFromHeight">The lowest block searched, for <see cref="FundingSpendStatus.SpentNotFound"/>.</param>
/// <param name="Error">Why the chain could not be read.</param>
/// <param name="FloorHeight">For <see cref="FundingSpendStatus.SpentNotFound"/>: the lowest block the spend can be in
/// (the funding block, or the funding height recorded in the backup); 0 when unknown.</param>
public sealed record FundingSpendLocation(
    FundingSpendStatus Status,
    OutpointSpentEventArgs? Spend = null,
    uint SearchedFromHeight = 0,
    string? Error = null,
    uint FloorHeight = 0)
{
    /// <summary>
    /// True for <see cref="FundingSpendStatus.SpentNotFound"/> when older blocks are left to search (the spend is
    /// below <see cref="SearchedFromHeight"/> and at or above <see cref="FloorHeight"/>).
    /// </summary>
    public bool HasOlderBlocksToSearch =>
        Status == FundingSpendStatus.SpentNotFound && SearchedFromHeight > FloorHeight;
}