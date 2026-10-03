using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Offers.Constants;

/// <summary>
/// The TLV types of BOLT 12 offers, invoice_requests, invoices and invoice_errors (<c>12-offer-encoding.md</c>, the
/// <c>offer</c>, <c>invoice_request</c>, <c>invoice</c> and <c>invoice_error</c> TLV streams).
/// </summary>
/// <remarks>
/// An invoice_request copies the offer's types and an invoice copies the invoice_request's, so the numbers are shared
/// across the three streams. <c>invoice_error</c> has its own namespace.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class Bolt12TlvTypes
{
    // offer (1-79 and 1,000,000,000-1,999,999,999)
    public const ulong OfferChains = 2;
    public const ulong OfferMetadata = 4;
    public const ulong OfferCurrency = 6;
    public const ulong OfferAmount = 8;
    public const ulong OfferDescription = 10;
    public const ulong OfferFeatures = 12;
    public const ulong OfferAbsoluteExpiry = 14;
    public const ulong OfferPaths = 16;
    public const ulong OfferIssuer = 18;
    public const ulong OfferQuantityMax = 20;
    public const ulong OfferIssuerId = 22;

    // invoice_request (0-159 and 1,000,000,000-2,999,999,999)
    public const ulong InvreqMetadata = 0;
    public const ulong InvreqChain = 80;
    public const ulong InvreqAmount = 82;
    public const ulong InvreqFeatures = 84;
    public const ulong InvreqQuantity = 86;
    public const ulong InvreqPayerId = 88;
    public const ulong InvreqPayerNote = 89;
    public const ulong InvreqPaths = 90;
    public const ulong InvreqBip353Name = 91;

    // invoice (0-239 and 1,000,000,000-3,999,999,999)
    public const ulong InvoicePaths = 160;
    public const ulong InvoiceBlindedPay = 162;
    public const ulong InvoiceCreatedAt = 164;
    public const ulong InvoiceRelativeExpiry = 166;
    public const ulong InvoicePaymentHash = 168;
    public const ulong InvoiceAmount = 170;
    public const ulong InvoiceFallbacks = 172;
    public const ulong InvoiceFeatures = 174;
    public const ulong InvoiceNodeId = 176;

    // invoice_request and invoice
    public const ulong Signature = 240;

    // invoice_error
    public const ulong ErroneousField = 1;
    public const ulong SuggestedValue = 3;
    public const ulong Error = 5;
}