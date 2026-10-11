namespace NLightning.Application.Onchain.Interfaces;

using Domain.Bitcoin.Events;

/// <summary>
/// Reacts to a transaction spending a channel's funding output (BOLT 5 plan O2-T5, §3.2 step 3): classify it, persist
/// the close record and the outputs to resolve with <c>OnchainResolving</c> in one save, tell the peer, and hand the
/// channel to the resolution executor.
/// </summary>
public interface IOnchainChannelWatcher
{
    /// <summary>
    /// Handles one funding spend (idempotent: a replayed block changes nothing). Mutual closes are left to the channel
    /// manager's close path. Takes the channel's lock itself; the caller must hold none.
    /// </summary>
    /// <returns>What was recorded (null when the spend was not handled: unknown channel, not a funding spend, a mutual
    /// close).</returns>
    Task<FundingSpendOutcome?> HandleFundingSpentAsync(OutpointSpentEventArgs args,
                                                       CancellationToken cancellationToken = default);
}

/// <summary>What <see cref="IOnchainChannelWatcher.HandleFundingSpentAsync"/> recorded.</summary>
/// <param name="Kind">The classification of the spend.</param>
/// <param name="OutputCount">How many outputs to resolve were recorded.</param>
/// <param name="Replayed">True when the same spend was already recorded (nothing changed).</param>
public sealed record FundingSpendOutcome(Domain.Onchain.Enums.ChannelCloseKind Kind, int OutputCount, bool Replayed);