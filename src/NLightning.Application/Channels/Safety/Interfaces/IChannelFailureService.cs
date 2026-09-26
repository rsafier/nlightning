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
/// <para>Order: under the channel's lock, persist <c>ChannelState.Failed</c> with the <c>error</c> we send (re-sent
/// on every reconnection), then build and fully sign the latest local commitment with the peer's stored signature;
/// after the lock, publish it (the watch with its txid is saved before the publish) and send the <c>error</c> to the
/// peer if it is connected. Once the commitment reaches its confirmation depth the channel is persisted
/// <c>Closed</c>. Sweeping its outputs (to_local after the delay, HTLC outputs) is BOLT 5 (NL-094) and not done.</para>
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

    /// <summary>Subscribes to the chain monitor's confirmations (our commitment confirmed → <c>Closed</c>).</summary>
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
                                           string? RequirementId = null);

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
    NotApplicable = 7
}

/// <summary>The outcome of failing a channel.</summary>
/// <param name="Status">What was done.</param>
/// <param name="CommitmentTxId">The txid of the commitment broadcast (or that would have been), when one was built.
/// </param>
public sealed record ChannelFailureOutcome(ChannelFailureStatus Status, TxId? CommitmentTxId);