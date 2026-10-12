namespace NLightning.Domain.Offers.Models;

using Payments.Models;

/// <summary>
/// Per-call limits of <c>IOfferPaymentService.PayOfferAsync</c>: how the invoice is fetched and how it is paid.
/// </summary>
public sealed record PayOfferOptions
{
    /// <summary>
    /// The limits of the payment itself (fee, parts, timeout), as for <c>IPaymentService.PayInvoiceAsync</c>.
    /// </summary>
    public PayInvoiceOptions Payment { get; init; } = new();

    /// <summary>
    /// How long each invoice_request waits for its invoice or invoice_error.
    /// </summary>
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How many invoice_requests are sent at most, each over the next <c>offer_paths</c> path or a fresh reply path
    /// (BOLT 4: a sender SHOULD retry over another path).
    /// </summary>
    public int MaxFetchAttempts { get; init; } = 3;
}