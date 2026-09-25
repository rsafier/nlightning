using System.Collections.Immutable;

namespace NLightning.Domain.Channels.Commitments.Events;

using Crypto.ValueObjects;
using Enums;
using ValueObjects;

/// <summary>
/// Derives the <see cref="IChannelDomainEvent"/>s from HTLC states: what an engine operation raised
/// (<see cref="FromChange"/>) and what is still pending in persisted records (<see cref="DerivePending(ChannelId, IEnumerable{HtlcRecord})"/>,
/// plan invariant I8).
/// </summary>
/// <remarks>
/// Both use the same rules, so every event the engine raises is also derived from the records it asked to persist:
/// <list type="bullet">
/// <item><see cref="IncomingHtlcLockedIn"/>: an incoming HTLC in <see cref="HtlcState.RcvdAddAckRevocation"/>.</item>
/// <item><see cref="OutgoingHtlcFulfilled"/>: an outgoing HTLC whose preimage we know.</item>
/// <item><see cref="OutgoingHtlcFailed"/>: an outgoing HTLC in <see cref="HtlcState.RcvdRemoveAckRevocation"/>
/// removed by a failure.</item>
/// <item><see cref="OutgoingHtlcSettled"/>: an outgoing HTLC in <see cref="HtlcState.RcvdRemoveAckRevocation"/>.</item>
/// </list>
/// </remarks>
public static class ChannelDomainEvents
{
    /// <summary>
    /// The events still pending in persisted HTLC records, to replay on startup: pass every open HTLC record and every
    /// settled record the persistence layer has not pruned yet. The consumers must be idempotent (see
    /// <see cref="IChannelDomainEvent"/>).
    /// </summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="htlcs">Open and settled-but-unpruned HTLC records, in any order.</param>
    /// <returns>The pending events ordered by HTLC (direction, id); for one HTLC, fulfilled or failed before settled.
    /// </returns>
    /// <exception cref="ArgumentException">A record has a legacy or unknown state.</exception>
    public static IReadOnlyList<IChannelDomainEvent> DerivePending(ChannelId channelId, IEnumerable<HtlcRecord> htlcs)
    {
        ArgumentNullException.ThrowIfNull(htlcs);
        var events = new List<IChannelDomainEvent>();
        foreach (var htlc in htlcs.OrderBy(h => h.Key))
        {
            if (!HtlcStateTable.IsDefined(htlc.State))
                throw new ArgumentException($"HTLC {htlc.Key} has legacy or unknown state {htlc.State}", nameof(htlcs));

            if (htlc.Direction == HtlcDirection.Incoming)
            {
                if (htlc.State == HtlcState.RcvdAddAckRevocation)
                    events.Add(new IncomingHtlcLockedIn(channelId, htlc));
                continue;
            }

            if (PreimageOf(htlc) is { } preimage)
                events.Add(new OutgoingHtlcFulfilled(channelId, htlc.Id, htlc.PaymentHash, preimage));
            if (HtlcStateTable.IsFinal(htlc.State))
                AddSettled(events, channelId, htlc);
        }

        return events;
    }

    /// <summary>
    /// The events still pending in a restored snapshot plus the settled records not pruned yet (see
    /// <see cref="DerivePending(ChannelId, IEnumerable{HtlcRecord})"/>).
    /// </summary>
    public static IReadOnlyList<IChannelDomainEvent> DerivePending(ChannelCommitments commitments,
                                                                   IEnumerable<HtlcRecord>? unprunedSettledHtlcs = null)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        var records = unprunedSettledHtlcs is null
                          ? commitments.Htlcs.Values
                          : commitments.Htlcs.Values.Concat(unprunedSettledHtlcs);
        return DerivePending(commitments.ChannelId, records);
    }

    /// <summary>
    /// The events one engine operation raises: an incoming HTLC that reached lock-in, an outgoing HTLC whose preimage is
    /// newly known, and every outgoing HTLC that settled (failed first when it was a failure).
    /// </summary>
    internal static IReadOnlyList<IChannelDomainEvent> FromChange(
        ChannelId channelId, ImmutableSortedDictionary<HtlcKey, HtlcRecord> before,
        ImmutableSortedDictionary<HtlcKey, HtlcRecord> after, IReadOnlyList<HtlcRecord> settled)
    {
        List<IChannelDomainEvent>? events = null;
        var changed = settled.Count == 0
                          ? after.Values
                          : after.Values.Concat(settled).OrderBy(h => h.Key);
        foreach (var htlc in changed)
        {
            before.TryGetValue(htlc.Key, out var old);
            if (ReferenceEquals(old, htlc))
                continue;

            if (htlc.Direction == HtlcDirection.Incoming)
            {
                if (htlc.State == HtlcState.RcvdAddAckRevocation && old?.State != HtlcState.RcvdAddAckRevocation)
                    (events ??= []).Add(new IncomingHtlcLockedIn(channelId, htlc));
                continue;
            }

            if (PreimageOf(htlc) is { } preimage && (old is null || PreimageOf(old) is null))
                (events ??= []).Add(new OutgoingHtlcFulfilled(channelId, htlc.Id, htlc.PaymentHash, preimage));
            if (HtlcStateTable.IsFinal(htlc.State))
                AddSettled(events ??= [], channelId, htlc);
        }

        return events ?? (IReadOnlyList<IChannelDomainEvent>)[];
    }

    private static void AddSettled(List<IChannelDomainEvent> events, ChannelId channelId, HtlcRecord htlc)
    {
        var removal = htlc.Removal
                   ?? throw new ArgumentException($"Settled HTLC {htlc.Key} has no removal", nameof(htlc));
        if (!removal.IsFulfill)
            events.Add(new OutgoingHtlcFailed(channelId, htlc.Id, htlc.PaymentHash, removal));
        events.Add(new OutgoingHtlcSettled(channelId, htlc.Id, htlc.PaymentHash, removal.Kind));
    }

    private static Secret? PreimageOf(HtlcRecord htlc) => htlc.KnownPreimage ?? htlc.Removal?.PaymentPreimage;
}