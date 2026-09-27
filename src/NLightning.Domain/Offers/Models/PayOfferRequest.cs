namespace NLightning.Domain.Offers.Models;

using Money;

/// <summary>
/// What <c>IOfferPaymentService</c> asks for in the invoice_request it sends (BOLT 12 "Invoice Requests" writer).
/// </summary>
/// <param name="Offer">The <c>lno1...</c> string.</param>
/// <param name="Amount"><c>invreq_amount</c>: required when the offer has no amount or is in another currency;
/// otherwise null or at least the offer's amount times the quantity.</param>
/// <param name="Quantity"><c>invreq_quantity</c>: required when the offer has <c>offer_quantity_max</c>, forbidden
/// otherwise.</param>
/// <param name="PayerNote"><c>invreq_payer_note</c>, or null.</param>
public sealed record PayOfferRequest(string Offer, LightningMoney? Amount = null, ulong? Quantity = null,
                                     string? PayerNote = null);