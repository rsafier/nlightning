namespace NLightning.Domain.Client.Responses;

using Crypto.ValueObjects;
using Money;
using Payments.Enums;
using Payments.Models;

/// <summary>
/// One of our invoices, as returned by <c>CreateInvoice</c> and <c>ListInvoices</c>. It never carries the preimage or
/// the payment secret beyond what the BOLT 11 string already contains.
/// </summary>
public sealed class InvoiceInfoClientResponse
{
    public required string Bolt11 { get; init; }
    public required Hash PaymentHash { get; init; }

    /// <summary>
    /// The requested amount, or null for an any-amount invoice.
    /// </summary>
    public LightningMoney? Amount { get; init; }

    public string? Description { get; init; }
    public required InvoiceStatus Status { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// True when the invoice was still open at <see cref="ExpiresAt"/> when the response was built.
    /// </summary>
    public bool IsExpired { get; init; }

    /// <summary>
    /// The amount the paying HTLC carried, once accepted.
    /// </summary>
    public LightningMoney? AmountReceived { get; init; }

    public DateTimeOffset? SettledAt { get; init; }

    /// <summary>
    /// Maps a stored invoice; <paramref name="now"/> decides <see cref="IsExpired"/>.
    /// </summary>
    public static InvoiceInfoClientResponse FromModel(InvoiceModel invoice, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return new InvoiceInfoClientResponse
        {
            Bolt11 = invoice.Bolt11,
            PaymentHash = invoice.PaymentHash,
            Amount = invoice.Amount,
            Description = invoice.Description,
            Status = invoice.Status,
            CreatedAt = invoice.CreatedAt,
            ExpiresAt = invoice.ExpiresAt,
            IsExpired = invoice.IsExpired(now),
            AmountReceived = invoice.AmountReceived,
            SettledAt = invoice.SettledAt
        };
    }
}