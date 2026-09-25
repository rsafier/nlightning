namespace NLightning.Domain.Client.Responses;

using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;
using Payments.Enums;
using Payments.Models;
using Protocol.Onion.Enums;

/// <summary>
/// One of our outgoing payments, as returned by <c>PayInvoice</c> and <c>ListPayments</c>.
/// </summary>
public sealed class PaymentInfoClientResponse
{
    public required Hash PaymentHash { get; init; }
    public string? Bolt11 { get; init; }
    public required CompactPubKey PayeeNodeId { get; init; }

    /// <summary>
    /// What the payee receives.
    /// </summary>
    public required LightningMoney Amount { get; init; }

    /// <summary>
    /// Routing fees paid to intermediate hops.
    /// </summary>
    public required LightningMoney Fee { get; init; }

    public required PaymentStatus Status { get; init; }

    /// <summary>
    /// The preimage, once succeeded (the proof of payment).
    /// </summary>
    public Secret? Preimage { get; init; }

    /// <summary>
    /// The BOLT 4 failure code decoded at the origin, when the payment failed with a readable error.
    /// </summary>
    public FailureCode? FailureCode { get; init; }

    /// <summary>
    /// The route index of the failing node (0 = our peer), when attributable.
    /// </summary>
    public int? FailureSourceIndex { get; init; }

    public string? FailureReason { get; init; }
    public ChannelId? OutgoingChannelId { get; init; }
    public ulong? OutgoingHtlcId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public static PaymentInfoClientResponse FromModel(PaymentModel payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return new PaymentInfoClientResponse
        {
            PaymentHash = payment.PaymentHash,
            Bolt11 = payment.Bolt11,
            PayeeNodeId = payment.PayeeNodeId,
            Amount = payment.Amount,
            Fee = payment.Fee,
            Status = payment.Status,
            Preimage = payment.Preimage,
            FailureCode = payment.FailureCode,
            FailureSourceIndex = payment.FailureSourceIndex,
            FailureReason = payment.FailureReason,
            OutgoingChannelId = payment.OutgoingChannelId,
            OutgoingHtlcId = payment.OutgoingHtlcId,
            CreatedAt = payment.CreatedAt,
            CompletedAt = payment.CompletedAt
        };
    }
}