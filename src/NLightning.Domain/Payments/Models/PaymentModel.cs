namespace NLightning.Domain.Payments.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Money;
using Protocol.Onion.Enums;

/// <summary>
/// One of our outgoing payments: a single HTLC (no MPP) sent over one of our channels, directly to the payee or along
/// the invoice's route hints.
/// </summary>
/// <remarks>
/// <para>The payment is persisted (<c>IPaymentDbRepository</c>) as <see cref="PaymentStatus.InFlight"/> before its
/// HTLC is offered, and the HTLC carries <c>HtlcOrigin.Local(PaymentHash)</c>, so after a restart the outcome still
/// reaches the payment. There is at most one stored payment per <see cref="PaymentHash"/>: the latest attempt. A
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

    public CompactPubKey PayeeNodeId { get; }

    /// <summary>
    /// The amount the payee receives.
    /// </summary>
    public LightningMoney Amount { get; }

    /// <summary>
    /// The routing fees paid to intermediate hops (zero for a direct payment).
    /// </summary>
    public LightningMoney Fee { get; }

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

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// The route the onion was built for, first hop (our peer) first and the payee last, with each hop's Sphinx shared
    /// secret; empty when it was not recorded. Persisted with the payment so a failure returned after a restart can
    /// still be decrypted and attributed.
    /// </summary>
    public IReadOnlyList<PaymentHop> Route { get; }

    /// <summary>
    /// The shared secret of each hop of <see cref="Route"/>, first hop first (for
    /// <c>IFailureOnionService.DecryptErrorPacket</c>).
    /// </summary>
    public IReadOnlyList<Secret> HopSharedSecrets => Route.Select(h => h.SharedSecret).ToList();

    /// <param name="paymentHash">The payment hash.</param>
    /// <param name="bolt11">The BOLT 11 invoice paid, if any.</param>
    /// <param name="payeeNodeId">The payee.</param>
    /// <param name="amount">The amount the payee receives.</param>
    /// <param name="fee">The routing fees.</param>
    /// <param name="createdAt">When the payment was created.</param>
    /// <param name="route">The route of the onion (see <see cref="Route"/>); null or empty when not recorded. When
    /// given, its last hop must be <paramref name="payeeNodeId"/>.</param>
    public PaymentModel(Hash paymentHash, string? bolt11, CompactPubKey payeeNodeId, LightningMoney amount,
                        LightningMoney fee, DateTimeOffset createdAt, IReadOnlyList<PaymentHop>? route = null)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(fee);
        if (amount.IsZero)
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment amount must be positive.");
        if (route is { Count: > 0 } && route[^1].NodeId != payeeNodeId)
            throw new ArgumentException("The last hop of the route must be the payee.", nameof(route));

        PaymentHash = paymentHash;
        Bolt11 = bolt11;
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
                                       IReadOnlyList<PaymentHop>? route = null)
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

        return new PaymentModel(paymentHash, bolt11, payeeNodeId, amount, fee, createdAt, route)
        {
            Status = status,
            OutgoingChannelId = outgoingChannelId,
            OutgoingHtlcId = outgoingHtlcId,
            Preimage = preimage,
            FailureCode = failureCode,
            FailureSourceIndex = failureSourceIndex,
            FailureReason = failureReason,
            CompletedAt = completedAt
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
    /// The HTLC failed irrevocably, or could not be offered (then <paramref name="failureCode"/> is null).
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
        Status = PaymentStatus.Failed;
    }
}