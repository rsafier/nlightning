namespace NLightning.Domain.Offers.Models;

/// <summary>
/// The BOLT 12 side of a payment we made to an offer (<c>PaymentModel.Bolt12</c>; BOLT 12 plan §3.8 step 5, §3.10).
/// </summary>
/// <param name="Offer">The <c>lno1...</c> string paid.</param>
/// <param name="InvoiceBytes">The invoice's TLV stream we paid.</param>
/// <param name="InvoiceRequestMetadata">Our <c>invreq_metadata</c>: the transient payer key is derived from it (plan
/// D3), so it is kept for a future payer proof.</param>
/// <param name="PayerNote">Our <c>invreq_payer_note</c>, or null.</param>
public sealed record Bolt12PaymentDetails(string Offer, ReadOnlyMemory<byte> InvoiceBytes,
                                          ReadOnlyMemory<byte> InvoiceRequestMetadata, string? PayerNote = null);