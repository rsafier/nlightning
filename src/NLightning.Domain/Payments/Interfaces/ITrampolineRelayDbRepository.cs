namespace NLightning.Domain.Payments.Interfaces;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Trampoline;

/// <summary>
/// Stores trampoline relays (NL-875) and their incoming parts: the relay keyed by its payment hash, each part by its
/// incoming (channel, HTLC id).
/// </summary>
/// <remarks>
/// <para>Writes are staged: they reach the database with <c>IUnitOfWork.SaveChangesAsync</c>. The lookups by key
/// (<see cref="GetAsync"/>, <see cref="GetPartsAsync"/>, <see cref="GetPartAsync"/>) see what this unit of work
/// staged; the listings read what is saved.</para>
/// <para>Order of saves (the relay engine's contract): a part is added in the save that accepts its incoming HTLC into
/// the relay (with the relay itself for the first part); the relay is marked <c>Sending</c> before its first outgoing
/// HTLC is offered (each offer carries <c>HtlcOrigin.Trampoline(paymentHash)</c>); <c>Fulfilled</c> is saved with the
/// preimage on every incoming part's record (<c>KnownPreimage</c>, NL-322) before any part is fulfilled; <c>Failed</c>
/// only once no outgoing HTLC with the relay's origin is unresolved.</para>
/// </remarks>
public interface ITrampolineRelayDbRepository
{
    /// <summary>Stages a new relay. Its payment hash must be new.</summary>
    /// <exception cref="InvalidOperationException">A relay with the same payment hash exists.</exception>
    Task AddAsync(TrampolineRelayModel relay);

    /// <summary>Stages the relay's mutable fields (status, fee, outgoing payment secret, preimage, failure,
    /// completion time).</summary>
    /// <exception cref="InvalidOperationException">The relay does not exist.</exception>
    Task UpdateAsync(TrampolineRelayModel relay);

    /// <summary>Stages a new incoming part. Its relay must exist (saved or staged) and its incoming HTLC be new.
    /// </summary>
    /// <exception cref="InvalidOperationException">No relay for its payment hash, or the incoming HTLC already is a
    /// part.</exception>
    Task AddPartAsync(TrampolineRelayPartModel part);

    /// <summary>
    /// Stages the removal of the <c>Failed</c> relay of <paramref name="paymentHash"/> and its parts, so a new attempt
    /// of the payer with the same payment hash can start a new relay (as a retried payment replaces a failed one; the
    /// relay engine calls it only once every part's incoming HTLC is resolved and no outgoing HTLC of the relay is
    /// unresolved). Save it before adding the new relay. In the same save the relay is kept as a
    /// <see cref="TrampolineRelayAttemptModel"/> (the next attempt number of the hash), so <c>listforwards</c> keeps
    /// its history (NL-899); nothing else ever reads those rows.
    /// </summary>
    /// <exception cref="InvalidOperationException">No relay for the hash, or it is not <c>Failed</c>.</exception>
    Task RemoveFailedAsync(Hash paymentHash) =>
        throw new NotSupportedException("This repository cannot remove a failed trampoline relay.");

    /// <summary>
    /// One page of the failed attempts that a payer's retry replaced (NL-899), newest first, under the filters of
    /// <paramref name="query"/>: creation time, a status other than <c>Failed</c> matches none, an incoming channel
    /// matches an attempt with a part on it. Reads what is saved. The default (test doubles) keeps none.
    /// </summary>
    Task<IReadOnlyList<TrampolineRelayAttemptModel>> ListReplacedAttemptsAsync(
        TrampolineRelayListQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrampolineRelayAttemptModel>>([]);

    /// <summary>
    /// The counts by status and the fees earned of the relays matching <paramref name="query"/> (its page ignored),
    /// the replaced attempts counted as failed (NL-981). Reads what is saved.
    /// </summary>
    /// <exception cref="NotSupportedException">The repository cannot sum relays (test doubles).</exception>
    Task<TrampolineRelayTotals> SummarizeAsync(TrampolineRelayListQuery query,
                                               CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository cannot sum trampoline relays.");

    /// <summary>The relay of <paramref name="paymentHash"/> with its parts (ordered by channel and HTLC id), or null.
    /// </summary>
    Task<(TrampolineRelayModel Relay, IReadOnlyList<TrampolineRelayPartModel> Parts)?> GetAsync(Hash paymentHash);

    /// <summary>The incoming parts of the relay of <paramref name="paymentHash"/>, ordered by channel and HTLC id
    /// (empty when there is none).</summary>
    Task<IReadOnlyList<TrampolineRelayPartModel>> GetPartsAsync(Hash paymentHash);

    /// <summary>The part whose incoming HTLC is <paramref name="htlcId"/> on <paramref name="channelId"/>, or null (the
    /// HTLC is not part of a trampoline relay).</summary>
    Task<TrampolineRelayPartModel?> GetPartAsync(ChannelId channelId, ulong htlcId);

    /// <summary>The relays that are <c>Collecting</c> or <c>Sending</c>, oldest first (the startup replay).</summary>
    Task<IReadOnlyList<TrampolineRelayModel>> ListUnfinishedAsync();

    /// <summary>One page of relays matching <paramref name="query"/>, newest first.</summary>
    Task<IReadOnlyList<TrampolineRelayModel>> ListAsync(TrampolineRelayListQuery query,
                                                        CancellationToken cancellationToken = default);
}