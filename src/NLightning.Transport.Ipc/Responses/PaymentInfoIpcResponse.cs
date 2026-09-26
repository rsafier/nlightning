using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Channels.ValueObjects;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Protocol.Onion.Enums;

/// <summary>
/// One of our outgoing payments, in a <see cref="PayInvoiceIpcResponse"/> or a <see cref="ListPaymentsIpcResponse"/>.
/// </summary>
[MessagePackObject]
public sealed class PaymentInfoIpcResponse
{
    [Key(0)] public required Hash PaymentHash { get; init; }
    [Key(1)] public string? Bolt11 { get; init; }
    [Key(2)] public required CompactPubKey PayeeNodeId { get; init; }

    /// <summary>
    /// What the payee receives.
    /// </summary>
    [Key(3)] public required LightningMoney Amount { get; init; }

    /// <summary>
    /// Routing fees paid to intermediate hops.
    /// </summary>
    [Key(4)] public required LightningMoney Fee { get; init; }

    [Key(5)] public required PaymentStatus Status { get; init; }

    /// <summary>
    /// The preimage (the proof of payment), once succeeded.
    /// </summary>
    [Key(6)] public Secret? Preimage { get; init; }

    /// <summary>
    /// The BOLT 4 failure code decoded at the origin. It crosses the wire as its <c>u16</c>, so a code this build does
    /// not name still arrives.
    /// </summary>
    [Key(7)] public FailureCode? FailureCode { get; init; }

    /// <summary>
    /// The route index of the failing node (0 = our peer), when attributable.
    /// </summary>
    [Key(8)] public int? FailureSourceIndex { get; init; }

    [Key(9)] public string? FailureReason { get; init; }
    [Key(10)] public ChannelId? OutgoingChannelId { get; init; }
    [Key(11)] public ulong? OutgoingHtlcId { get; init; }
    [Key(12)] public required DateTimeOffset CreatedAt { get; init; }
    [Key(13)] public DateTimeOffset? CompletedAt { get; init; }

    public static PaymentInfoIpcResponse FromClientResponse(PaymentInfoClientResponse payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        return new PaymentInfoIpcResponse
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