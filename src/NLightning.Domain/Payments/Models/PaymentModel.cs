namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Keysend;
using Money;
using Offers.Models;
using Protocol.Onion.Enums;

/// <summary>
/// One of our outgoing payments (the stored row of one payment hash): its full amount and one recorded part, a route
/// and HTLC sent over one of our channels, directly to the payee or along the invoice's route hints.
/// </summary>
/// <remarks>
/// <para>Since ABCD wave 6 a payment may be split into several parts (<c>basic_mpp</c>, NL-270) and retried. The row
/// holds <see cref="Amount"/> for the whole payment but only one part: <see cref="Route"/>, its shared secrets and
/// <see cref="OutgoingChannelId"/>/<see cref="OutgoingHtlcId"/> are those of a part that was offered (rewritten to a
/// live part when the recorded one fails while others are in flight), and <see cref="Fee"/> is the fee of the parts in
/// flight (on success, those the payee settled; zero once failed). The other parts live only in the sending session's
/// memory and are not persisted (NL-321): their outcomes still reach the payment after a restart through their
/// <c>HtlcOrigin.Local(PaymentHash)</c>, but their failures can no longer be decrypted.</para>
/// <para>The payment is persisted (<c>IPaymentDbRepository</c>) as <see cref="PaymentStatus.InFlight"/> before its
/// first HTLC is offered, and every HTLC carries <c>HtlcOrigin.Local(PaymentHash)</c>, so after a restart the outcome
/// still reaches the payment. There is at most one stored payment per <see cref="PaymentHash"/>: the latest attempt. A
/// retry of a <see cref="PaymentStatus.Failed"/> payment is a new <see cref="PaymentModel"/> that replaces the failed
/// one (<c>IPaymentDbRepository.AddAsync</c>); an in-flight or succeeded payment is never retried.</para>
/// <para>Status moves only from <see cref="PaymentStatus.InFlight"/> to <see cref="PaymentStatus.Succeeded"/> or
/// <see cref="PaymentStatus.Failed"/>; the mutators throw <see cref="InvalidOperationException"/> otherwise.</para>
/// </remarks>
public sealed class PaymentModel
{
    public Hash PaymentHash { get; }

    /// <summary>
    /// The BOLT 11 invoice paid, when the payment came from one.
    /// </summary>
    public string? Bolt11 { get; }

    /// <summary>
    /// The BOLT 12 offer and invoice paid, when the payment came from an offer (<c>payoffer</c>).
    /// </summary>
    public Bolt12PaymentDetails? Bolt12 { get; }

    /// <summary>
    /// The custom records of a spontaneous (keysend) payment, when the payment is one: it pays no invoice
    /// (<see cref="Bolt11"/> and <see cref="Bolt12"/> are null) and the preimage was ours.
    /// </summary>
    public KeysendDetails? Keysend { get; }

    public CompactPubKey PayeeNodeId { get; }

    /// <summary>
    /// The amount the payee receives.
    /// </summary>
    public LightningMoney Amount { get; }

    /// <summary>
    /// The routing fees paid to intermediate hops (zero for a direct payment): while in flight those of the parts in
    /// flight, on success those the payee settled, and zero once the payment failed, since nothing was paid (NL-982).
    /// </summary>
    public LightningMoney Fee { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public PaymentStatus Status { get; private set; }

    /// <summary>
    /// The channel the HTLC was offered on, once offered.
    /// </summary>
    public ChannelId? OutgoingChannelId { get; private set; }

    /// <summary>
    /// The id of our HTLC on <see cref="OutgoingChannelId"/>, once offered.
    /// </summary>
    public ulong? OutgoingHtlcId { get; private set; }

    /// <summary>
    /// The preimage, once <see cref="PaymentStatus.Succeeded"/>.
    /// </summary>
    public Secret? Preimage { get; private set; }

    /// <summary>
    /// The BOLT 4 failure code decoded at the origin, when the failure was readable.
    /// </summary>
    public FailureCode? FailureCode { get; private set; }

    /// <summary>
    /// The route index of the node that produced the failure (0 = our peer, the route length - 1 = the payee), when
    /// it is attributable.
    /// </summary>
    public int? FailureSourceIndex { get; private set; }

    /// <summary>
    /// A local description of why it failed (never sent to anyone).
    /// </summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// The failure reason of a payment failed at startup because its HTLCs were gone and their outcomes unknown
    /// (NL-321): it may still turn out paid when a fulfill is replayed, so it is not a final failure to report
    /// (NL-1001).
    /// </summary>
    public const string UnknownOutcomeReason = "No part of the payment was still in flight after the restart; its "
                                             + "stored parts' HTLCs are gone and their outcomes are unknown.";

    /// <summary>
    /// Whether the payment failed only because its outcome was unknown after a restart (<see cref="UnknownOutcomeReason"/>):
    /// not a final failure (NL-1001).
    /// </summary>
    public bool IsOutcomeUnknown =>
        Status == PaymentStatus.Failed && FailureCode is null && FailureReason == UnknownOutcomeReason;

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// The route the onion was built for, first hop (our peer) first and the payee last, with each hop's Sphinx shared
    /// secret; empty when it was not recorded. Persisted with the payment so a failure returned after a restart can
    /// still be decrypted and attributed.
    /// </summary>
    public IReadOnlyList<PaymentHop> Route { get; private set; }

    /// <summary>
    /// The shared secret of each hop of <see cref="Route"/>, first hop first (for
    /// <c>IFailureOnionService.DecryptErrorPacket</c>).
    /// </summary>
    public IReadOnlyList<Secret> HopSharedSecrets => Route.Select(h => h.SharedSecret).ToList();

    /// <summary>
    /// The operator's label (NL-602 A3-T1, migration <c>AddAccountingFinancial</c>; at most 256 UTF-8 bytes), or null.
    /// The accounting writers copy it into the event's details.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// The operator's tags as one canonical <c>k=v</c> list (NL-602 A3-T1, at most 1 KiB), or null.
    /// </summary>
    public string? Tags { get; set; }

    /// <summary>
    /// The payment is the outgoing leg of a trampoline relay (NL-875), not our own spend: its HTLCs carry
    /// <c>HtlcOrigin.Trampoline(PaymentHash)</c>, and the accounting books it through the relay's
    /// <c>TrampolineRelaySettled</c> event instead of <c>PaymentSucceeded</c>/<c>PaymentFailed</c>. Written with the
    /// row (a retry's replacement keeps it).
    /// </summary>
    public bool IsTrampolineRelay { get; init; }

    /// <param name="paymentHash">The payment hash.</param>
    /// <param name="bolt11">The BOLT 11 invoice paid, if any.</param>
    /// <param name="payeeNodeId">The payee.</param>
    /// <param name="amount">The amount the payee receives.</param>
    /// <param name="fee">The routing fees.</param>
    /// <param name="createdAt">When the payment was created.</param>
    /// <param name="route">The route of the onion (see <see cref="Route"/>); null or empty when not recorded. When
    /// given, its last hop must be <paramref name="payeeNodeId"/>.</param>
    /// <param name="bolt12">The BOLT 12 offer and invoice paid, if any.</param>
    /// <param name="keysend">The custom records of a keysend payment, when it is one.</param>
    public PaymentModel(Hash paymentHash, string? bolt11, CompactPubKey payeeNodeId, LightningMoney amount,
                        LightningMoney fee, DateTimeOffset createdAt, IReadOnlyList<PaymentHop>? route = null,
                        Bolt12PaymentDetails? bolt12 = null, KeysendDetails? keysend = null)
    {
        if (keysend is not null && (bolt11 is not null || bolt12 is not null))
            throw new ArgumentException("A keysend payment pays no invoice.", nameof(keysend));
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(fee);
        if (amount.IsZero)
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment amount must be positive.");
        if (route is { Count: > 0 } && route[^1].NodeId != payeeNodeId)
            throw new ArgumentException("The last hop of the route must be the payee.", nameof(route));

        PaymentHash = paymentHash;
        Bolt11 = bolt11;
        Bolt12 = bolt12;
        Keysend = keysend;
        PayeeNodeId = payeeNodeId;
        Amount = amount;
        Fee = fee;
        CreatedAt = createdAt;
        Route = route is null ? [] : [.. route];
        Status = PaymentStatus.InFlight;
    }

    /// <summary>
    /// Rebuilds a stored payment in any state (persistence only). Validates that the fields match the status.
    /// </summary>
    public static PaymentModel Restore(Hash paymentHash, string? bolt11, CompactPubKey payeeNodeId,
                                       LightningMoney amount, LightningMoney fee, DateTimeOffset createdAt,
                                       PaymentStatus status, ChannelId? outgoingChannelId, ulong? outgoingHtlcId,
                                       Secret? preimage, FailureCode? failureCode, int? failureSourceIndex,
                                       string? failureReason, DateTimeOffset? completedAt,
                                       IReadOnlyList<PaymentHop>? route = null,
                                       Bolt12PaymentDetails? bolt12 = null, KeysendDetails? keysend = null,
                                       bool isTrampolineRelay = false)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown payment status.");
        if (outgoingChannelId is null != outgoingHtlcId is null)
            throw new ArgumentException("The outgoing channel and HTLC id are set together.", nameof(outgoingHtlcId));
        if (status == PaymentStatus.Succeeded && preimage is null)
            throw new ArgumentException("A succeeded payment needs its preimage.", nameof(preimage));
        if (status != PaymentStatus.Succeeded && preimage is not null)
            throw new ArgumentException("Only a succeeded payment has a preimage.", nameof(preimage));
        if (status != PaymentStatus.InFlight && completedAt is null)
            throw new ArgumentException("A completed payment needs its completion time.", nameof(completedAt));

        return new PaymentModel(paymentHash, bolt11, payeeNodeId, amount, fee, createdAt, route, bolt12, keysend)
        {
            Status = status,
            OutgoingChannelId = outgoingChannelId,
            OutgoingHtlcId = outgoingHtlcId,
            Preimage = preimage,
            FailureCode = failureCode,
            FailureSourceIndex = failureSourceIndex,
            FailureReason = failureReason,
            CompletedAt = completedAt,
            IsTrampolineRelay = isTrampolineRelay
        };
    }

    /// <summary>
    /// The total amount our HTLC carries: <see cref="Amount"/> + <see cref="Fee"/>.
    /// </summary>
    public LightningMoney TotalAmount => Amount + Fee;

    /// <summary>
    /// Records the HTLC that carries the payment. Throws if one is already recorded.
    /// </summary>
    public void AddOutgoingHtlc(ChannelId channelId, ulong htlcId)
    {
        if (Status != PaymentStatus.InFlight)
            throw new InvalidOperationException($"Cannot attach an HTLC to a payment that is {Status}.");
        if (OutgoingHtlcId is not null)
            throw new InvalidOperationException("The payment already has an outgoing HTLC.");

        OutgoingChannelId = channelId;
        OutgoingHtlcId = htlcId;
    }

    /// <summary>
    /// The payee revealed <paramref name="preimage"/> (the caller checked SHA256(preimage) == payment hash).
    /// </summary>
    public void Succeed(Secret preimage, DateTimeOffset completedAt)
    {
        if (Status != PaymentStatus.InFlight)
            throw new InvalidOperationException($"Cannot complete a payment that is {Status}.");

        Preimage = preimage;
        CompletedAt = completedAt;
        Status = PaymentStatus.Succeeded;
    }

    /// <summary>
    /// Records the hold times the hops of <see cref="Route"/> reported in a verified <c>attribution_data</c> (BOLT 4),
    /// first hop first: hop <c>i</c> gets <c>holdTimes[i]</c>; hops past the list (not verified) keep what they had.
    /// </summary>
    /// <param name="holdTimes">The verified hold times, first hop first.</param>
    /// <returns>True when a hop changed.</returns>
    public bool RecordHoldTimes(IReadOnlyList<TimeSpan> holdTimes)
    {
        ArgumentNullException.ThrowIfNull(holdTimes);

        var changed = false;
        var route = Route.ToList();
        for (var i = 0; i < route.Count && i < holdTimes.Count; i++)
        {
            if (route[i].HoldTime == holdTimes[i])
                continue;

            route[i] = route[i] with { HoldTime = holdTimes[i] };
            changed = true;
        }

        if (changed)
            Route = route;
        return changed;
    }

    /// <summary>
    /// The HTLC failed irrevocably, or could not be offered (then <paramref name="failureCode"/> is null). The fee
    /// becomes zero: a failed payment paid none (NL-982; the fee the last attempt offered is only logged).
    /// </summary>
    /// <remarks>
    /// <c>IChannelOperations.OfferHtlcAsync</c> saves the add and only then returns the HTLC id, so recording it with
    /// <see cref="AddOutgoingHtlc"/> is a later save. An <see cref="PaymentStatus.InFlight"/> payment without
    /// <see cref="OutgoingHtlcId"/> (after a crash, or on a startup replay) may therefore still have a live HTLC:
    /// fail it without a failure code only once no channel HTLC carries <c>HtlcOrigin.Local(PaymentHash)</c>, because
    /// a later <see cref="Succeed"/> on a failed payment throws and the preimage would be recorded nowhere.
    /// </remarks>
    public void Fail(FailureCode? failureCode, int? failureSourceIndex, string? failureReason,
                     DateTimeOffset completedAt)
    {
        if (Status != PaymentStatus.InFlight)
            throw new InvalidOperationException($"Cannot fail a payment that is {Status}.");

        FailureCode = failureCode;
        FailureSourceIndex = failureSourceIndex;
        FailureReason = failureReason;
        CompletedAt = completedAt;
        Fee = LightningMoney.Zero;
        Status = PaymentStatus.Failed;
    }
}