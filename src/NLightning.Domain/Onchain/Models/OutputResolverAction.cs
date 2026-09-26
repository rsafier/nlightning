namespace NLightning.Domain.Onchain.Models;

using Channels.Commitments.Events;
using Persistence.Interfaces;

/// <summary>
/// One thing an <see cref="Interfaces.IOutputResolver"/> asks the on-chain resolution executor to do. The executor
/// stages every write of one round in one save, then publishes, watches, raises and alerts (see the resolver
/// remarks).
/// </summary>
public abstract record OutputResolverAction;

/// <summary>
/// Stage a new output row (for example the output of a confirmed second-level transaction) or the new state of a known
/// one (<see cref="Interfaces.IOnchainResolutionDbRepository.UpsertOutputAsync"/>).
/// </summary>
/// <param name="Output">The row to write.</param>
public sealed record UpsertOutputAction(OutputResolutionModel Output) : OutputResolverAction;

/// <summary>
/// Stage a fully signed transaction for broadcast (its <see cref="BroadcastTransactionModel"/> row, skipped when a row
/// for the txid exists) and publish it after the save. Pair it with an <see cref="UpsertOutputAction"/> that sets the
/// output's <see cref="OutputResolutionModel.ResolvingTransactionId"/> and
/// <see cref="Enums.OutputResolutionState.Broadcast"/> so the decision and the transaction commit together.
/// </summary>
/// <param name="Transaction">The transaction and its purpose.</param>
public sealed record BroadcastAction(BroadcastTransactionModel Transaction) : OutputResolverAction;

/// <summary>
/// Stage a watch for an outpoint (normally <see cref="Enums.WatchedOutpointPurpose.ResolutionOutput"/>) and start it
/// after the save; skipped when the outpoint is watched already.
/// </summary>
/// <param name="Watch">The watch.</param>
public sealed record WatchOutpointAction(WatchedOutpointModel Watch) : OutputResolverAction;

/// <summary>
/// Hand an event to the HTLC switch after the save (for example <see cref="OutgoingHtlcFulfilled"/> for a preimage
/// seen on chain, or <see cref="OutgoingHtlcFailed"/> once a timeout is at reasonable depth). The switch is
/// idempotent; the resolver repeats the event until the output's row says it was raised.
/// </summary>
/// <param name="Event">The event.</param>
public sealed record RaiseChannelEventAction(IChannelDomainEvent Event) : OutputResolverAction;

/// <summary>
/// Stage any other write in the round's unit of work (for example a preimage learned on chain, persisted before the
/// fulfill is raised, BOLT2 I10). It runs after the upserts and before the save; it must only stage, never save.
/// </summary>
/// <param name="Description">What it writes, for logs.</param>
/// <param name="Stage">Stages the write.</param>
public sealed record StageWriteAction(string Description, Func<IUnitOfWork, CancellationToken, Task> Stage)
    : OutputResolverAction;

/// <summary>
/// Log a critical alert for the operator (B5-GEN-06 unknown spend, B5-RMT-03 data loss, funds lost to the peer).
/// </summary>
/// <param name="RequirementId">The BOLT 5 plan requirement behind it (<c>B5-…</c>).</param>
/// <param name="Message">What happened.</param>
public sealed record AlertAction(string RequirementId, string Message) : OutputResolverAction;