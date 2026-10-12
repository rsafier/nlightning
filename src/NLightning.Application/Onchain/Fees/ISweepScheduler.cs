namespace NLightning.Application.Onchain.Fees;

using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// Fee bumping of our unconfirmed sweeps, claims and penalties (BOLT 5 plan §3.7, O6-T1, NL-317): called by the on-chain
/// resolution executor once per channel round, after the resolver, under the channel's lock.
/// </summary>
public interface ISweepScheduler
{
    /// <summary>
    /// The actions that replace (RBF) every pending sweep, claim or penalty of the channel that stayed unconfirmed too
    /// long (<see cref="Domain.Onchain.Fees.SweepFeePolicy.ShouldBump"/>), and that retire the pending ones that can no
    /// longer confirm. Stages nothing itself: the executor applies the actions in the round's one save, then publishes.
    /// </summary>
    /// <param name="close">The channel's recorded funding spend.</param>
    /// <param name="outputs">The channel's output rows with this round's updates applied.</param>
    /// <param name="height">The tip.</param>
    /// <param name="unitOfWork">The round's unit of work (read only).</param>
    /// <param name="cancellationToken">Cancels the planning.</param>
    Task<IReadOnlyList<OutputResolverAction>> PlanAsync(ChannelCloseModel close,
                                                        IReadOnlyList<OutputResolutionModel> outputs, uint height,
                                                        IUnitOfWork unitOfWork, CancellationToken cancellationToken);
}