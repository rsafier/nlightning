namespace NLightning.Application.Channels.Backup.Models;

using Domain.Bitcoin.Events;

/// <summary>What <see cref="Interfaces.IFundingSpendLocator"/> found for one funding output.</summary>
public enum FundingSpendStatus
{
    /// <summary>The funding output is confirmed and unspent: the chain monitor sees its spend.</summary>
    Unspent,

    /// <summary>The backup has no short channel id: the funding may not be confirmed, so nothing can be told.</summary>
    NotConfirmed,

    /// <summary>The output is spent and the spending transaction was found (<see cref="FundingSpendLocation.Spend"/>).</summary>
    SpentFound,

    /// <summary>The output is spent but its spend is not in the searched blocks: rescan from
    /// <see cref="FundingSpendLocation.SearchedFromHeight"/> or below.</summary>
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
public sealed record FundingSpendLocation(
    FundingSpendStatus Status,
    OutpointSpentEventArgs? Spend = null,
    uint SearchedFromHeight = 0,
    string? Error = null);