namespace NLightning.Application.Channels.Safety.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// Fails a channel (BOLT 1/2 "fail the channel", BOLT2 plan N9-T4, D10): the <b>only</b> path that broadcasts our
/// commitment transaction, so "never broadcast a revoked commitment" (I4) and "never broadcast after data loss" (I12)
/// are enforced in one place (and again by the signer).
/// </summary>
/// <remarks>
/// <para>Order: under the channel's lock, build and fully sign the latest local commitment with the peer's stored
/// signature, then persist <c>ChannelState.Failed</c> with the <c>error</c> we send (re-sent on every reconnection)
/// and the commitment's broadcast row (purpose <c>LocalCommitment</c>, with its number) in one save (NL-271); after
/// the lock, publish it and send the <c>error</c> to the peer if it is connected. Once a commitment spends the funding
/// output the on-chain watcher moves the channel to <c>OnchainResolving</c> (BOLT 5 plan O2-T5).</para>
/// <para>Callers must not hold any channel lock. Idempotent: failing a failed channel again re-sends its stored
/// error and, when a broadcast is asked for, rebroadcasts the same commitment.</para>
/// </remarks>
public interface IChannelFailureService
{
    /// <summary>
    /// Fails <paramref name="channelId"/> and, unless <see cref="ChannelFailureRequest.Broadcast"/> is false or data
    /// loss was detected, broadcasts our latest local commitment.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The channel is not loaded.</exception>
    Task<ChannelFailureOutcome> FailChannelAsync(ChannelId channelId, ChannelFailureRequest request,
                                                 CancellationToken cancellationToken = default);

    /// <summary>
    /// The seam for a <see cref="ChannelFailedException"/> the channel manager already persisted: broadcasts when
    /// <see cref="ChannelFailedException.MustBroadcast"/> is set (the stored error is kept).
    /// </summary>
    Task<ChannelFailureOutcome> FailChannelAsync(ChannelFailedException failure,
                                                 CancellationToken cancellationToken = default);

    /// <summary>
    /// The first half of <see cref="FailChannelAsync(ChannelId, ChannelFailureRequest, CancellationToken)"/> for a
    /// caller that already holds the channel's lock (the channel manager, when a handler throws a
    /// <see cref="ChannelFailedException"/> with <see cref="ChannelFailedException.MustBroadcast"/>; NL-271): builds and
    /// signs our latest commitment and persists Failed, the error and the commitment's broadcast row in one save. Pass
    /// the result to <see cref="CompleteFailureAsync"/> after releasing the lock.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The channel is not loaded.</exception>
    Task<PreparedChannelFailure> PrepareFailureUnderLockAsync(ChannelId channelId, ChannelFailureRequest request,
                                                              CancellationToken cancellationToken = default);

    /// <summary>
    /// The second half: publishes the prepared commitment (outside every channel lock) and, when
    /// <paramref name="sendError"/>, sends the error to the peer.
    /// </summary>
    Task<ChannelFailureOutcome> CompleteFailureAsync(PreparedChannelFailure prepared, bool sendError,
                                                     CancellationToken cancellationToken = default);

    /// <summary>Subscribes to new blocks (the retry of a refused publish) and resumes interrupted broadcasts.</summary>
    void Start();

    /// <summary>Unsubscribes.</summary>
    void Stop();
}

/// <summary>
/// Why and how to fail a channel.
/// </summary>
/// <param name="Reason">The local log text.</param>
/// <param name="PeerMessage">The <c>error</c> data sent to the peer (never our internal details).</param>
/// <param name="Broadcast">Broadcast our latest commitment (false only when the peer must close, e.g. data loss).</param>
/// <param name="RequirementId">The <c>B2-*</c> row that asked for the failure, when there is one.</param>
public sealed record ChannelFailureRequest(string Reason, string PeerMessage, bool Broadcast = true,
                                           string? RequirementId = null)
{
    /// <summary>
    /// Checked under the channel's lock before anything is done: false means the reason went away while the caller
    /// waited for the lock (e.g. the peer's reply to a timed-out <c>closing_signed</c> arrived), and the call returns
    /// <see cref="ChannelFailureStatus.NotApplicable"/> without touching anything, not even the per-block retry of an
    /// earlier failure's refused publish. Null for an unconditional failure.
    /// </summary>
    public Func<Domain.Channels.Models.ChannelModel, bool>? StillApplies { get; init; }
}

/// <summary>What <see cref="IChannelFailureService.FailChannelAsync(ChannelId, ChannelFailureRequest, CancellationToken)"/>
/// did.</summary>
public enum ChannelFailureStatus : byte
{
    /// <summary>Our commitment was published (first time in this process).</summary>
    Broadcast = 1,

    /// <summary>Its watch already existed (published before): it was sent again.</summary>
    Rebroadcast = 2,

    /// <summary>Failed (error persisted) without a broadcast, as asked.</summary>
    FailedWithoutBroadcast = 3,

    /// <summary>Data loss: failed, and the broadcast refused (I12).</summary>
    RefusedDataLoss = 4,

    /// <summary>Failed, but no commitment could be built or signed (no stored signature, a refusing signer).</summary>
    NoBroadcastableCommitment = 5,

    /// <summary>Failed and signed, but publishing it failed (retried on the next failure call, e.g. the next block).
    /// </summary>
    PublishFailed = 6,

    /// <summary>The channel is already closed or was never open: nothing done.</summary>
    NotApplicable = 7,

    /// <summary>
    /// Our commitment can never confirm: the funding output was spent by another transaction (its broadcast row is
    /// abandoned). Nothing sent.
    /// </summary>
    Superseded = 8
}

/// <summary>
/// A failure persisted under the channel's lock by
/// <see cref="IChannelFailureService.PrepareFailureUnderLockAsync"/>, to be completed after the lock with
/// <see cref="IChannelFailureService.CompleteFailureAsync"/>.
/// </summary>
public sealed class PreparedChannelFailure
{
    internal PreparedChannelFailure(ChannelId channelId, ChannelFailureRequest request)
    {
        ChannelId = channelId;
        Request = request;
    }

    public ChannelId ChannelId { get; }

    public ChannelFailureRequest Request { get; }

    /// <summary>Set when nothing is left to do after the lock (not applicable, precondition gone).</summary>
    public ChannelFailureOutcome? EarlyOutcome { get; internal set; }

    /// <summary>The outcome decided under the lock when there is no commitment to publish.</summary>
    internal ChannelFailureOutcome? DecidedOutcome { get; set; }

    internal Domain.Channels.Models.ChannelModel? Channel { get; set; }
    internal Domain.Protocol.Messages.ErrorMessage? Error { get; set; }
    internal SignedLocalCommitment? Commitment { get; set; }
    internal Domain.Onchain.Models.BroadcastTransactionModel? Broadcast { get; set; }
    internal bool BroadcastStaged { get; set; }
}

/// <summary>The outcome of failing a channel.</summary>
/// <param name="Status">What was done.</param>
/// <param name="CommitmentTxId">The txid of the commitment broadcast (or that would have been), when one was built.
/// </param>
public sealed record ChannelFailureOutcome(ChannelFailureStatus Status, TxId? CommitmentTxId);