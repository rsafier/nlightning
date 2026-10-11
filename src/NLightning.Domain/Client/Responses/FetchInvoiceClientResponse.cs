namespace NLightning.Domain.Client.Responses;

using Crypto.ValueObjects;
using Money;
using Offers.Enums;
using Offers.Models;

/// <summary>
/// How fetching an invoice for a BOLT 12 offer ended (<c>fetchinvoice</c>, and the first half of <c>payoffer</c>).
/// </summary>
public sealed class FetchInvoiceClientResponse
{
    public required FetchInvoiceStatus Status { get; init; }

    /// <summary>How many invoice_requests got an answer or timed out.</summary>
    public int Attempts { get; init; }

    /// <summary>The <c>invoice_error</c>'s text, or why the fetch failed.</summary>
    public string? Error { get; init; }

    /// <summary>The <c>invoice_error</c>'s <c>erroneous_field</c>.</summary>
    public ulong? ErroneousField { get; init; }

    /// <summary><c>invoice_node_id</c>, when an invoice was received.</summary>
    public CompactPubKey? NodeId { get; init; }

    /// <summary><c>invoice_amount</c>.</summary>
    public LightningMoney? Amount { get; init; }

    /// <summary><c>invoice_payment_hash</c>.</summary>
    public Hash? PaymentHash { get; init; }

    /// <summary><c>invoice_created_at</c>.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary><c>invoice_created_at</c> plus <c>invoice_relative_expiry</c>.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>How many <c>invoice_paths</c> the invoice carries.</summary>
    public int PathCount { get; init; }

    /// <summary>The invoice's TLV stream.</summary>
    public byte[]? Invoice { get; init; }

    public static FetchInvoiceClientResponse FromResult(FetchInvoiceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var invoice = result.Invoice;
        return new FetchInvoiceClientResponse
        {
            Status = result.Status,
            Attempts = result.Attempts,
            Error = result.Error,
            ErroneousField = result.ErroneousField,
            NodeId = invoice?.NodeId,
            Amount = invoice?.Amount,
            PaymentHash = invoice?.PaymentHash,
            CreatedAt = invoice?.CreatedAt,
            ExpiresAt = invoice?.ExpiresAt,
            PathCount = invoice?.PathCount ?? 0,
            Invoice = invoice?.InvoiceBytes.ToArray()
        };
    }
}