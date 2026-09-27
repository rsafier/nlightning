namespace NLightning.Domain.Channels.Quiescence;

using Crypto.ValueObjects;
using Exceptions;
using Models;
using Node;
using Protocol.Messages;
using Protocol.Payloads;
using ValueObjects;

/// <summary>
/// Owns the per-channel quiescence state (BOLT 2 "Channel Quiescence", <c>option_quiesce</c> bits 34/35; splicing plan
/// §3.2, wave Q). Implemented by <c>QuiescenceService</c> (Application, lane Q-B), a singleton keyed by channel id.
/// </summary>
/// <remarks>
/// <para>Lock discipline: the members marked "under the lock" are called by code that already holds the channel's
/// lock (<see cref="Interfaces.IChannelLockProvider"/>): the <c>stfu</c> handler, the commitment transition path and
/// the dependent protocols' handlers. <see cref="RequestAsync"/> takes the lock itself and must be called without
/// it.</para>
/// <para>The gate: while <see cref="QuiescenceState.BlocksNewLocalUpdates"/>, <c>IChannelOperations</c> refuses new
/// updates with <see cref="ChannelQuiescentException"/> (a retryable <see cref="CommitmentRefusedException"/>) and
/// <c>CommitScheduler</c> still signs and revokes what is pending (Q-S-04, Q-R-02). An <c>update_*</c> from a peer
/// whose <c>stfu</c> we received (<see cref="QuiescenceState.StfuReceived"/>) violates Q-S-04: warning and
/// disconnect.</para>
/// </remarks>
public interface IQuiescenceService
{
    /// <summary>
    /// The channel's quiescence state; <see cref="QuiescenceState.None"/> for a channel without one (or unknown).
    /// Reads only; changes happen under the channel's lock, so read it there when the answer must be current.
    /// </summary>
    QuiescenceState GetState(ChannelId channelId);

    /// <summary>
    /// Asks for quiescence on behalf of a dependent protocol: queues the request and sends <c>stfu</c> with
    /// <c>initiator</c> = 1 as soon as BOLT 2 allows it (Q-S-01: <c>option_quiesce</c> negotiated; Q-S-02: none of our
    /// updates pending for either side; <c>Open</c> and reestablished on this connection). Takes the channel's lock;
    /// never call it while holding one.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="purpose">The dependent protocol that will end the quiescence (Q-R-06).</param>
    /// <param name="cancellationToken">Withdraws the request while our <c>stfu</c> is not sent yet
    /// (<see cref="QuiescenceEndReason.RequestCancelled"/>); once sent, only the dependent protocol or a disconnection
    /// ends it.</param>
    /// <returns>Completes when the channel is quiescent, with the initiator (Q-R-05). <see cref="QuiescenceInitiator.Remote"/>
    /// means the peer's simultaneous request won (it is the funder): the caller must not start its protocol and waits
    /// for the peer's instead.</returns>
    /// <exception cref="InvalidOperationException"><c>option_quiesce</c> is not negotiated, the channel is not
    /// <c>Open</c>, a request or quiescence is already in progress, or the quiescence ended (disconnection) before
    /// the channel became quiescent.</exception>
    /// <exception cref="KeyNotFoundException">Unknown channel.</exception>
    Task<QuiescenceInitiator> RequestAsync(ChannelId channelId, QuiescencePurpose purpose,
                                           CancellationToken cancellationToken = default);

    /// <summary>
    /// Under the lock: applies a received <c>stfu</c> (Q-R-01, Q-R-02, Q-R-05).
    /// </summary>
    /// <param name="channel">The channel, as loaded under its lock.</param>
    /// <param name="stfu">The received payload.</param>
    /// <param name="negotiatedFeatures">The features negotiated with the peer.</param>
    /// <returns>Our reply <c>stfu</c> (<c>initiator</c> = 0) to send now, or null when it must wait until our pending
    /// changes are committed and revoked (it is then released by <see cref="TryReleaseStfu"/>) or when we already
    /// sent ours (the channel is now quiescent).</returns>
    /// <exception cref="WarningException">A <see cref="ChannelWarningException"/> with <c>CloseConnection</c> for a
    /// second <c>stfu</c> (Q-S-03) or a <c>stfu</c> without <c>option_quiesce</c> negotiated.</exception>
    StfuMessage? OnStfuReceived(ChannelModel channel, StfuPayload stfu, FeatureSet negotiatedFeatures);

    /// <summary>
    /// Under the lock, after every commitment transition (<c>commitment_signed</c>/<c>revoke_and_ack</c> sent or
    /// received): the <c>stfu</c> that is owed (our queued request with <c>initiator</c> = 1, or our reply with
    /// <c>initiator</c> = 0) if Q-S-02 now allows it, else null. Records it as sent; the caller must send it.
    /// </summary>
    StfuMessage? TryReleaseStfu(ChannelModel channel);

    /// <summary>
    /// Under the lock: ends the quiescence of <paramref name="channelId"/> (the dependent protocol finished, or
    /// <c>tx_abort</c>, or the timeout) and completes or faults a waiting <see cref="RequestAsync"/>. A channel
    /// without quiescence is ignored.
    /// </summary>
    void Terminate(ChannelId channelId, QuiescenceEndReason reason);

    /// <summary>
    /// The connection to <paramref name="peerPubKey"/> closed: every channel of that peer is no longer quiescing or
    /// quiescent (Q-R-04, <see cref="QuiescenceEndReason.Disconnected"/>). Needs no lock (a reconnection starts from
    /// <see cref="QuiescenceState.None"/>).
    /// </summary>
    void OnPeerDisconnected(CompactPubKey peerPubKey);
}