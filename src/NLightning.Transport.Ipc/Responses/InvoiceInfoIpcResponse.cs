using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;

/// <summary>
/// One of our invoices, in a <see cref="CreateInvoiceIpcResponse"/> or a <see cref="ListInvoicesIpcResponse"/>. It
/// never carries the preimage.
/// </summary>
[MessagePackObject]
public sealed class InvoiceInfoIpcResponse
{
    [Key(0)] public required string Bolt11 { get; init; }
    [Key(1)] public required Hash PaymentHash { get; init; }

    /// <summary>
    /// The requested amount, or null for an any-amount invoice.
    /// </summary>
    [Key(2)] public LightningMoney? Amount { get; init; }

    [Key(3)] public string? Description { get; init; }
    [Key(4)] public required InvoiceStatus Status { get; init; }
    [Key(5)] public required DateTimeOffset CreatedAt { get; init; }
    [Key(6)] public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// True when the invoice was still open at <see cref="ExpiresAt"/> when the daemon built the response.
    /// </summary>
    [Key(7)] public bool IsExpired { get; init; }

    /// <summary>
    /// The amount the paying HTLC carried, once accepted.
    /// </summary>
    [Key(8)] public LightningMoney? AmountReceived { get; init; }

    [Key(9)] public DateTimeOffset? SettledAt { get; init; }

    public static InvoiceInfoIpcResponse FromClientResponse(InvoiceInfoClientResponse invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new InvoiceInfoIpcResponse
        {
            Bolt11 = invoice.Bolt11,
            PaymentHash = invoice.PaymentHash,
            Amount = invoice.Amount,
            Description = invoice.Description,
            Status = invoice.Status,
            CreatedAt = invoice.CreatedAt,
            ExpiresAt = invoice.ExpiresAt,
            IsExpired = invoice.IsExpired,
            AmountReceived = invoice.AmountReceived,
            SettledAt = invoice.SettledAt
        };
    }
}