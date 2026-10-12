namespace NLightning.Domain.Offers.Models;

using Crypto.ValueObjects;

/// <summary>
/// The BOLT 12 side of an invoice we issued in answer to an invoice_request (<c>InvoiceModel.Bolt12</c>; BOLT 12 plan
/// §3.7 step 4, §3.10).
/// </summary>
/// <param name="OfferId">The offer the invoice_request was for.</param>
/// <param name="InvoiceBytes">The signed invoice's TLV stream, as sent.</param>
/// <param name="PayerId">The request's <c>invreq_payer_id</c>.</param>
/// <param name="Quantity">The request's <c>invreq_quantity</c>, or null.</param>
/// <param name="PayerNote">The request's <c>invreq_payer_note</c>, or null.</param>
public sealed record Bolt12InvoiceDetails(Hash OfferId, ReadOnlyMemory<byte> InvoiceBytes, CompactPubKey PayerId,
                                          ulong? Quantity = null, string? PayerNote = null);