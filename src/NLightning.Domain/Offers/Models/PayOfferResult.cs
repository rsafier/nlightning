namespace NLightning.Domain.Offers.Models;

using Payments.Models;

/// <summary>
/// The outcome of <c>IOfferPaymentService.PayOfferAsync</c>.
/// </summary>
/// <param name="Fetch">How the invoice was fetched.</param>
/// <param name="Payment">The payment of the fetched invoice, or null when no invoice was received (nothing was paid
/// or persisted).</param>
public sealed record PayOfferResult(FetchInvoiceResult Fetch, PayInvoiceResult? Payment);