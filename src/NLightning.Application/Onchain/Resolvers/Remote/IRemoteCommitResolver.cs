namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;

/// <summary>
/// Resolves the outputs of a peer commitment on chain (BOLT 5 plan O4; §Unilateral Close Handling: Remote Commitment
/// Transaction): the peer's current commitment (<see cref="ChannelCloseKind.RemoteCommitment"/>), the one we signed
/// whose <c>revoke_and_ack</c> is outstanding (<see cref="ChannelCloseKind.RemoteNextCommitment"/>, B5-RMT-01) and one
/// newer than any we know (<see cref="ChannelCloseKind.FutureCommitment"/>, data loss, B5-RMT-03).
/// </summary>
/// <remarks>
/// <para>
/// Contract with the on-chain watcher (O2-T5): every method runs under the channel's lock with the caller's scoped unit
/// of work; it stages rows (<c>OutputResolutions</c>, <c>WatchedOutpoints</c>, <c>BroadcastTransactions</c>) and
/// returns a <see cref="RemoteResolutionRound"/>. The caller saves, releases the lock, and then calls
/// <see cref="CompleteAsync"/>, which tracks the new watches, publishes the staged transactions and raises the switch
/// events. Every method is idempotent: a replayed block or spend changes nothing that is already recorded.
/// </para>
/// <para>
/// Keys are never stored: the peer's per-commitment point of the commitment on chain is kept with each output and the
/// signer re-derives our HTLC key from it (<c>SweepKeyKind.HtlcRemotePoint</c>); <c>to_remote</c> is signed with our
/// <c>payment_basepoint</c> key (static_remotekey), which needs no point, so it is swept after data loss too.
/// </para>
/// </remarks>
public interface IRemoteCommitResolver
{
    /// <summary>True for the close kinds this resolver handles.</summary>
    bool CanResolve(ChannelCloseKind kind);

    /// <summary>
    /// The peer's commitment <paramref name="close"/> confirmed: maps its outputs, stages one resolution row and one
    /// spend watch per output that pays us (or may: HTLC outputs), and runs the first resolution round at
    /// <paramref name="tipHeight"/>.
    /// </summary>
    /// <param name="channel">The channel with its commitment snapshot (the engine is stopped).</param>
    /// <param name="close">The recorded funding spend.</param>
    /// <param name="commitmentTransaction">The commitment on chain.</param>
    /// <param name="tipHeight">The current tip height.</param>
    /// <param name="unitOfWork">The caller's unit of work (saved by the caller).</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    Task<RemoteResolutionRound> BeginAsync(ChannelModel channel, ChannelCloseModel close, ChainTx commitmentTransaction,
                                           uint tipHeight, IUnitOfWork unitOfWork,
                                           CancellationToken cancellationToken);

    /// <summary>
    /// A watched output of the commitment was spent in a processed block: records who spent it and how (the preimage a
    /// witness revealed, checked against the payment hash, is saved before any upstream fulfill, BOLT2 I10), then runs
    /// a resolution round. A spend of an outpoint that is not one of this commitment's rows returns an empty round.
    /// </summary>
    Task<RemoteResolutionRound> OnOutputSpentAsync(ChannelModel channel, ChannelCloseModel close, TxId spentTxId,
                                                   uint spentOutputIndex, ChainTx spender, uint spendHeight,
                                                   uint tipHeight, IUnitOfWork unitOfWork,
                                                   CancellationToken cancellationToken);

    /// <summary>A new tip: runs the planner for every output of the commitment and acts on its answer.</summary>
    Task<RemoteResolutionRound> ResolveAsync(ChannelModel channel, ChannelCloseModel close, uint tipHeight,
                                             IUnitOfWork unitOfWork, CancellationToken cancellationToken);

    /// <summary>
    /// After the caller's save and outside the channel lock: tracks the round's watches, publishes its transactions
    /// (a refused send stays pending and is rebroadcast by the chain monitor) and raises its switch events.
    /// </summary>
    Task CompleteAsync(RemoteResolutionRound round, CancellationToken cancellationToken);
}