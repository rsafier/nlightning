namespace NLightning.Domain.Offers.Models;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// A BOLT 12 invoice received for one of our invoice_requests that passed the invoice reader checks (BOLT 12
/// "Invoices" reader, B12-INV-03/04/05).
/// </summary>
/// <param name="InvoiceBytes">The invoice's TLV stream, as received.</param>
/// <param name="InvoiceRequestBytes">The invoice_request's TLV stream we sent.</param>
/// <param name="NodeId"><c>invoice_node_id</c>, whose signature verified.</param>
/// <param name="Amount"><c>invoice_amount</c>.</param>
/// <param name="PaymentHash"><c>invoice_payment_hash</c>.</param>
/// <param name="CreatedAt"><c>invoice_created_at</c>.</param>
/// <param name="RelativeExpirySeconds"><c>invoice_relative_expiry</c> (7200 when absent).</param>
/// <param name="PathCount">How many <c>invoice_paths</c> it carries.</param>
public sealed record FetchedBolt12Invoice(ReadOnlyMemory<byte> InvoiceBytes, ReadOnlyMemory<byte> InvoiceRequestBytes,
                                          CompactPubKey NodeId, LightningMoney Amount, Hash PaymentHash,
                                          DateTimeOffset CreatedAt, uint RelativeExpirySeconds, int PathCount)
{
    /// <summary>
    /// When the invoice expires (BOLT 12: <c>invoice_created_at + invoice_relative_expiry</c>).
    /// </summary>
    public DateTimeOffset ExpiresAt => CreatedAt.AddSeconds(RelativeExpirySeconds);
}