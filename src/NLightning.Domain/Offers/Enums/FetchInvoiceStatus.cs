namespace NLightning.Domain.Offers.Enums;

/// <summary>
/// How fetching an invoice for an offer ended (BOLT 12 "Offers" reader and "Invoices" reader; <c>payoffer</c>,
/// <c>fetchinvoice</c>).
/// </summary>
public enum FetchInvoiceStatus : byte
{
    /// <summary>
    /// An invoice arrived through our <c>reply_path</c> and passed every invoice reader check.
    /// </summary>
    Received = 0,

    /// <summary>
    /// The offer's owner answered with an <c>invoice_error</c>.
    /// </summary>
    InvoiceError = 1,

    /// <summary>
    /// No reply within the fetch timeout, on any attempt.
    /// </summary>
    TimedOut = 2,

    /// <summary>
    /// The invoice_request could not be sent (onion messages off, no path to the offer's issuer or introduction node).
    /// </summary>
    Unreachable = 3,

    /// <summary>
    /// A reply arrived but failed the BOLT 12 invoice reader checks (B12-INV-03/04), on every attempt.
    /// </summary>
    InvalidInvoice = 4
}