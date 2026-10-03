namespace NLightning.Domain.Offers.Models;

using Enums;

/// <summary>
/// How fetching an invoice for an offer ended.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Invoice">The verified invoice when <paramref name="Status"/> is
/// <see cref="FetchInvoiceStatus.Received"/>, else null.</param>
/// <param name="Attempts">How many invoice_requests were sent.</param>
/// <param name="Error">The <c>invoice_error</c>'s <c>error</c> text, or a local reason for the other failures.</param>
/// <param name="ErroneousField">The <c>invoice_error</c>'s <c>erroneous_field</c>, when it had one.</param>
public sealed record FetchInvoiceResult(FetchInvoiceStatus Status, FetchedBolt12Invoice? Invoice, int Attempts,
                                        string? Error = null, ulong? ErroneousField = null);