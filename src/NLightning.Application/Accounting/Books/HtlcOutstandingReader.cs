using Microsoft.Extensions.Logging;

namespace NLightning.Application.Accounting.Books;

using Domain.Accounting.Constants;
using Domain.Accounting.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;

/// <summary>
/// What the books' channels balance holds for HTLCs whose settle is booked while their channel has not folded them
/// into its balance yet (NL-886): reported apart as outstanding by the reconcile, never as drift.
/// </summary>
/// <param name="Msat">Books minus node that those HTLCs explain: + for an incoming HTLC (booked as ours, still in the
/// peer's gross balance), − for an outgoing one (booked as gone, still in our gross balance).</param>
/// <param name="HtlcCount">How many HTLCs hold some of it.</param>
/// <param name="ChannelCount">On how many channels.</param>
internal sealed record HtlcOutstanding(long Msat, int HtlcCount, int ChannelCount)
{
    public static HtlcOutstanding None { get; } = new(0, 0, 0);
}

/// <summary>
/// Reads, for the channels the reconcile's channels line counts, the HTLCs of the snapshot that are not final and whose
/// settle the books already booked (NL-886): the settle is booked when a fulfill is sent or received (the
/// <c>InvoiceSettled</c> in our fulfill's save, the <c>PaymentSucceeded</c> and <c>ForwardSettled</c> when the
/// switch handles the peer's fulfill), but the commitment engine moves the amount between the gross balances only once
/// the removal is irrevocably committed in both commitments, a few round trips later.
/// </summary>
/// <remarks>
/// <para><b>Per HTLC</b>, by the exact key of the event that books its settle (indexed lookups, bounded by the
/// snapshot's live HTLCs, never by history): an incoming HTLC by <c>ForwardSettled</c> of (its channel, its id), else
/// by the interceptor's <c>InterceptedHtlcSettled</c> of the same (NL-1182), else by our invoice's
/// <c>InvoiceSettled</c> of its payment hash; an outgoing HTLC by its stored origin (<c>IChannelStateDbRepository.GetHtlcOriginAsync</c>, written in the add's save): a forward by the
/// <c>ForwardSettled</c> of its incoming HTLC, our payment (or no origin, NL-265) by the <c>PaymentSucceeded</c> of the
/// origin's hash, a trampoline relay's (NL-875) by its <c>TrampolineRelaySettled</c> (incoming HTLCs too, by hash). So a forward is counted on each of its channels by its own HTLC: the upstream HTLC while its fulfill
/// is not committed (even before it is sent, the link down), the downstream one while the peer's fulfill is not.
/// An HTLC being failed is never counted: no settle of it can be booked.</para>
/// <para>The marker on the HTLC (<see cref="InFlightHtlcBucket.PreimageKnown"/>) is deliberately not required: the
/// reconcile takes the snapshot before it seals and projects the feed, so an event committed between the two is booked
/// while the snapshot shows its HTLC as it was before the save (the settling part of an invoice without its fulfill, a
/// part of a split payment the payee has not fulfilled yet).</para>
/// <para>Only what is booked is counted, and only for HTLCs the node still holds outside its balances, so a real
/// drift (a settle booked twice or for the wrong amount, a missing event once the HTLC is folded) stays visible.</para>
/// </remarks>
internal static class HtlcOutstandingReader
{
    public static async Task<HtlcOutstanding> ReadAsync(IUnitOfWork unitOfWork,
                                                        IEnumerable<ChannelBalanceBucket> channels, ILogger logger,
                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(channels);

        var books = unitOfWork.AccountingBooksDbRepository;
        var booked = new Dictionary<string, bool>(StringComparer.Ordinal);

        async Task<bool> IsBookedAsync(string key)
        {
            if (!booked.TryGetValue(key, out var found))
            {
                found = await books.GetEntryByKeyAsync(key, cancellationToken) is not null;
                booked[key] = found;
            }

            return found;
        }

        long total = 0;
        var htlcCount = 0;
        var channelCount = 0;
        foreach (var channel in channels)
        {
            if (channel.Htlcs is not { Count: > 0 } htlcs)
                continue;

            var holding = false;
            foreach (var htlc in htlcs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (htlc.IsFailing)
                    continue;

                var key = htlc.Direction == HtlcDirection.Incoming
                              ? await IncomingKeyAsync(channel.ChannelId, htlc, IsBookedAsync)
                              : await OutgoingKeyAsync(unitOfWork, channel.ChannelId, htlc, IsBookedAsync);
                if (key is null)
                    continue;

                var amount = htlc.Direction == HtlcDirection.Incoming ? htlc.AmountMsat : -htlc.AmountMsat;
                total = checked(total + amount);
                htlcCount++;
                holding = true;
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.LogDebug("Accounting reconcile: {Direction} HTLC {HtlcId} of channel {ChannelId} is settled "
                                  + "in the books ({EventKey}) but not committed yet: {AmountMsat} msat outstanding",
                                    htlc.Direction, htlc.HtlcId, channel.ChannelId, key, amount);
            }

            if (holding)
                channelCount++;
        }

        return new HtlcOutstanding(total, htlcCount, channelCount);
    }

    /// <summary>The booked key of an incoming HTLC's settle, or null.</summary>
    private static async Task<string?> IncomingKeyAsync(ChannelId channelId, InFlightHtlcBucket htlc,
                                                        Func<string, Task<bool>> isBooked)
    {
        var forward = AccountingEventKeys.ForwardSettled(channelId, htlc.HtlcId);
        if (await isBooked(forward))
            return forward;

        // NL-1182: a forward the HTLC interceptor settled books the whole HTLC, keyed like a forward's
        var intercepted = AccountingEventKeys.InterceptedHtlcSettled(channelId, htlc.HtlcId);
        if (await isBooked(intercepted))
            return intercepted;

        var invoice = AccountingEventKeys.InvoiceSettled(htlc.PaymentHash);
        if (await isBooked(invoice))
            return invoice;

        // A trampoline relay (NL-875) books its settle once per payment hash, like a forward's fee
        var trampoline = AccountingEventKeys.TrampolineRelaySettled(htlc.PaymentHash);
        return await isBooked(trampoline) ? trampoline : null;
    }

    /// <summary>The booked key of an outgoing HTLC's settle (by its stored origin), or null.</summary>
    private static async Task<string?> OutgoingKeyAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                        InFlightHtlcBucket htlc, Func<string, Task<bool>> isBooked)
    {
        var origin = await unitOfWork.ChannelStateDbRepository.GetHtlcOriginAsync(
                         channelId, new HtlcKey(HtlcDirection.Outgoing, htlc.HtlcId));
        var key = origin switch
        {
            {
                Kind: HtlcOriginKind.Forwarded, IncomingChannelId: { } incomingChannelId,
                IncomingHtlcId: { } incomingHtlcId
            } => AccountingEventKeys.ForwardSettled(incomingChannelId, incomingHtlcId),
            { Kind: HtlcOriginKind.Local, PaymentHash: { } paymentHash } =>
                AccountingEventKeys.PaymentSucceeded(paymentHash),
            { Kind: HtlcOriginKind.Trampoline, PaymentHash: { } paymentHash } =>
                AccountingEventKeys.TrampolineRelaySettled(paymentHash),
            null => AccountingEventKeys.PaymentSucceeded(htlc.PaymentHash),
            _ => null
        };

        return key is not null && await isBooked(key) ? key : null;
    }
}