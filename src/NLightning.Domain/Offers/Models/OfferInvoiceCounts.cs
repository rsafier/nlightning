namespace NLightning.Domain.Offers.Models;

/// <summary>
/// How many BOLT 12 invoices we issued for an offer, by outcome (for <c>listoffers</c> and the per-offer cap of unpaid
/// invoices, BOLT 12 plan D11).
/// </summary>
/// <param name="Paid">Settled invoices.</param>
/// <param name="Unpaid">
/// Open invoices that have not expired, plus <c>Accepted</c> ones (an HTLC set is held for them, expired or not, until it
/// settles or fails back), so partially paid MPP sets count against the caps too.
/// </param>
public sealed record OfferInvoiceCounts(int Paid, int Unpaid);