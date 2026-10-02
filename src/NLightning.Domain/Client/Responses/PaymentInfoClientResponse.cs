namespace NLightning.Domain.Client.Responses;

using Accounting.Labels;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Money;
using Payments.Enums;
using Payments.Keysend;
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

    /// <summary>
    /// True for a spontaneous (keysend) payment (lane lh1-l3).
    /// </summary>
    public bool IsKeysend { get; init; }

    /// <summary>
    /// The custom records sent with a keysend payment (empty for other payments).
    /// </summary>
    public IReadOnlyList<CustomRecord> CustomRecords { get; init; } = [];

    /// <summary>
    /// The operator's label (NL-602 A3-T1), or null.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c>, sorted by key (NL-602 A3-T1); empty for none.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

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
            CompletedAt = payment.CompletedAt,
            IsKeysend = payment.Keysend is not null,
            CustomRecords = payment.Keysend?.CustomRecords ?? [],
            Label = payment.Label,
            Tags = SourceLabels.FromStored(null, payment.Tags).TagStrings
        };
    }
}