namespace NLightning.Domain.Channels.Interfaces;

using Commitments;
using Crypto.ValueObjects;
using Models;
using Payments.ValueObjects;
using ValueObjects;

/// <summary>
/// Persists the commitment state machine of a channel (plan N5-T2): HTLCs with their state, fee updates, the local,
/// remote and unacked remote commitments, the channel's commitment scalars, the sent <c>commitment_signed</c> diff and
/// the peer's shachain.
/// </summary>
/// <remarks>
/// <para>
/// Every write is staged on the unit of work; the caller commits one transition with exactly one
/// <c>IUnitOfWork.SaveChangesAsync</c> (decision D3), then swaps the in-memory snapshot
/// (<see cref="ChannelModel.UpdateCommitments"/>, invariant I2), then sends (invariant I1). Rows are written one by one
/// by primary key, never as an entity graph.
/// </para>
/// <para>
/// Once a channel has a snapshot, its balances, next HTLC ids, commitment and revocation numbers are owned by this
/// repository: <c>IChannelDbRepository.UpdateAsync</c> no longer writes them.
/// </para>
/// </remarks>
public interface IChannelStateDbRepository
{
    /// <summary>
    /// Stages the whole snapshot: every HTLC, the fee updates, the three commitments and the scalars. Use it when a
    /// channel gets its first snapshot (e.g. after <c>funding_signed</c>/<c>channel_ready</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel does not exist.</exception>
    Task InitializeAsync(ChannelCommitments snapshot, ChannelStateExtras? extras = null);

    /// <summary>
    /// Stages what <paramref name="transition"/> says changed, taking the values from <paramref name="next"/>:
    /// upserted and settled HTLCs are upserted (settled ones keep their final state as an archive), dropped HTLCs are
    /// deleted, the fee updates are synchronized when they changed, and the commitments when they changed. The channel
    /// scalars and <paramref name="extras"/> are always written. A <see cref="ChannelTransition.RevokedRemoteCommit"/>
    /// with at least one HTLC is added to the revocation log in the same save (BOLT 5 plan O1-T1).
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel does not exist.</exception>
    Task ApplyAsync(ChannelCommitments next, ChannelTransition transition, ChannelStateExtras? extras = null);

    /// <summary>
    /// Reloads the commitment state of a channel, or null when it has no snapshot yet.
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="params">The static parameters (build them with <see cref="CommitmentParams.FromChannel"/>); they are not
    /// stored with the state.</param>
    /// <exception cref="InvalidOperationException">An HTLC row holds a legacy state (0-3) written before the state
    /// machine existed (NL-025), or a stored part is inconsistent: such a channel cannot be restored.</exception>
    Task<PersistedChannelState?> LoadAsync(ChannelId channelId, CommitmentParams @params);

    /// <summary>
    /// Stages the Sphinx shared secret of an HTLC (the incoming one we peeled), needed to wrap its failure after a
    /// restart (ONION M4). <see cref="ApplyAsync"/> never overwrites it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The HTLC row does not exist.</exception>
    Task SetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc, Secret sharedSecret);

    /// <summary>The Sphinx shared secret stored for an HTLC, if any.</summary>
    Task<Secret?> GetOnionSharedSecretAsync(ChannelId channelId, HtlcKey htlc);

    /// <summary>
    /// When the HTLC's row was first written: for an incoming HTLC the moment its <c>update_add_htlc</c> was received
    /// and persisted, for one we offered the moment we added it. It is the start of the BOLT 4 hold time
    /// (<c>attribution_data</c>, NL-326). <see cref="ApplyAsync"/> stamps it once, on insert, and never changes it.
    /// </summary>
    /// <returns>The time, or null when there is no row or the row predates the stamp (migration
    /// <c>AddAttributionData</c>).</returns>
    Task<DateTimeOffset?> GetHtlcAddedAtAsync(ChannelId channelId, HtlcKey htlc);

    /// <summary>
    /// Stages the <see cref="HtlcOrigin"/> of an HTLC we offered (<c>IChannelOperations.OfferHtlcAsync</c>): call it
    /// after <see cref="ApplyAsync"/> staged the add, in the same unit of work, so the origin commits with the add.
    /// <see cref="ApplyAsync"/> never overwrites it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not valid (<see cref="HtlcOrigin.IsValid"/>).
    /// </exception>
    /// <exception cref="InvalidOperationException">The HTLC row does not exist (staged or stored).</exception>
    Task SetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc, HtlcOrigin origin);

    /// <summary>The origin stored for an HTLC, or null (no row, or an HTLC without one).</summary>
    Task<HtlcOrigin?> GetHtlcOriginAsync(ChannelId channelId, HtlcKey htlc);

    /// <summary>
    /// The stored HTLCs, on any channel, that carry <paramref name="origin"/> (startup replay, ONION M4-T7: a
    /// <c>Pending</c> forward circuit or an <c>InFlight</c> payment without a recorded HTLC is failed only when this is
    /// empty). Archived (final) rows are included until pruned; check their state. A retry replaces a failed payment
    /// under the same payment hash (<c>IPaymentDbRepository.AddAsync</c>), so a <see cref="HtlcOrigin"/> of kind
    /// <c>Local</c> can also match the archived HTLCs of earlier failed attempts: a replay must count as "has an HTLC"
    /// only a row that is not final, or the one matching the payment's recorded outgoing channel and HTLC id, and
    /// otherwise fail the payment (else a retry that crashed before offering its HTLC stays in flight forever).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is not valid.</exception>
    Task<IReadOnlyList<(ChannelId ChannelId, HtlcKey Htlc)>> FindHtlcsByOriginAsync(HtlcOrigin origin);

    /// <summary>
    /// Stages the deletion of archived (final) HTLC rows once their events are handled. Non-final HTLCs are ignored.
    /// </summary>
    Task PruneSettledHtlcsAsync(ChannelId channelId, IEnumerable<HtlcKey> htlcs);
}