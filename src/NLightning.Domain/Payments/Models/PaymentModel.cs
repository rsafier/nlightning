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
/// reaches the payment. There is at most one payment per <see cref="PaymentHash"/>.</para>
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

    public PaymentModel(Hash paymentHash, string? bolt11, CompactPubKey payeeNodeId, LightningMoney amount,
                        LightningMoney fee, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentNullException.ThrowIfNull(fee);
        if (amount.IsZero)
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment amount must be positive.");

        PaymentHash = paymentHash;
        Bolt11 = bolt11;
        PayeeNodeId = payeeNodeId;
        Amount = amount;
        Fee = fee;
        CreatedAt = createdAt;
        Status = PaymentStatus.InFlight;
    }

    /// <summary>
    /// Rebuilds a stored payment in any state (persistence only). Validates that the fields match the status.
    /// </summary>
    public static PaymentModel Restore(Hash paymentHash, string? bolt11, CompactPubKey payeeNodeId,
                                       LightningMoney amount, LightningMoney fee, DateTimeOffset createdAt,
                                       PaymentStatus status, ChannelId? outgoingChannelId, ulong? outgoingHtlcId,
                                       Secret? preimage, FailureCode? failureCode, int? failureSourceIndex,
                                       string? failureReason, DateTimeOffset? completedAt)
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

        return new PaymentModel(paymentHash, bolt11, payeeNodeId, amount, fee, createdAt)
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