namespace NLightning.Domain.Onchain.Interfaces;

using Enums;
using Models;

/// <summary>
/// Resolves the outputs of one kind of funding spend (BOLT 5 plan §3.2 step 4, O3-O5): our commitment, the peer's
/// current or next commitment, a revoked commitment, a future commitment (data loss).
/// </summary>
/// <remarks>
/// <para>
/// The on-chain resolution executor (<c>Application/Onchain/OnchainResolutionExecutor</c>) owns the loop. For every
/// channel in <c>OnchainResolving</c> it calls <see cref="ResolveAsync"/> of the resolver whose
/// <see cref="CanResolve"/> accepts the close's <see cref="ChannelCloseModel.Kind"/>: once right after the funding spend
/// is classified, then after every processed block and once at startup. When a watched resolution output
/// (<see cref="WatchedOutpointPurpose.ResolutionOutput"/>) is spent it calls <see cref="OnOutputSpentAsync"/> first.
/// </para>
/// <para>
/// The executor applies the returned actions under the channel's lock, in one unit of work: every
/// <see cref="UpsertOutputAction"/>, the row of every <see cref="BroadcastAction"/> (skipped when a row for that txid
/// exists), every <see cref="WatchOutpointAction"/> and every <see cref="StageWriteAction"/> are staged and saved
/// together. Only after that save does it publish the broadcasts (<see cref="IChainBroadcaster.PublishAsync"/>),
/// start the new watches, raise the <see cref="RaiseChannelEventAction"/> events to the HTLC switch and log the
/// <see cref="AlertAction"/>s. A failed save applies none of them.
/// </para>
/// <para>
/// A resolver keeps no state: it decides from the persisted rows, the chain height and whatever it reads itself (the
/// channel, the revocation log, the watched outpoints), so it is called again with fresh facts every block and must
/// return the same actions until their effect is visible (planner rule, <c>OutputResolutionPlanner</c>). Repeated
/// actions are harmless: broadcasts are deduplicated by txid, watches by outpoint, switch events are idempotent.
/// The executor itself marks an output <see cref="OutputResolutionState.Resolved"/> when a spend confirms and
/// <see cref="OutputResolutionState.Irrevocable"/> once that spend is 100 blocks deep (O6-T2), and closes the channel
/// when every output is irrevocable or ignored; resolvers need not do either.
/// </para>
/// <para>
/// A resolver must not take the channel lock (the executor holds it), must not save (the executor saves), and must
/// not publish (persist before broadcast, D4).
/// </para>
/// </remarks>
public interface IOutputResolver
{
    /// <summary>True when this resolver handles funding spends of <paramref name="kind"/>.</summary>
    bool CanResolve(ChannelCloseKind kind);

    /// <summary>
    /// Decides what to do now for the outputs of a closed channel.
    /// </summary>
    /// <param name="close">The recorded funding spend.</param>
    /// <param name="outputs">Every output row of the channel (resolved ones included), as persisted, with the
    /// executor's per-block updates already applied.</param>
    /// <param name="height">The height of the last processed block (the tip).</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns>The actions to apply, in order (empty for none).</returns>
    Task<IReadOnlyList<OutputResolverAction>> ResolveAsync(ChannelCloseModel close,
                                                           IReadOnlyList<OutputResolutionModel> outputs, uint height,
                                                           CancellationToken cancellationToken);

    /// <summary>
    /// A transaction in a processed block spent one of the channel's resolution outputs (ours or the peer's
    /// transaction). Called before <see cref="ResolveAsync"/> for that block; also called again for a replayed block.
    /// </summary>
    /// <param name="close">The recorded funding spend.</param>
    /// <param name="output">The spent output's row, already marked <see cref="OutputResolutionState.Resolved"/> at
    /// <paramref name="height"/> by the executor.</param>
    /// <param name="spendingTransaction">The spending transaction (witnesses included, for preimage extraction).</param>
    /// <param name="height">The height of the block that holds it.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns>The actions to apply (for example a new row and watch for a second-level output, a switch event for
    /// a preimage seen on chain), in order.</returns>
    Task<IReadOnlyList<OutputResolverAction>> OnOutputSpentAsync(ChannelCloseModel close,
                                                                 OutputResolutionModel output,
                                                                 ChainTx spendingTransaction, uint height,
                                                                 CancellationToken cancellationToken);
}