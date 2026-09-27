namespace NLightning.Domain.Payments.Enums;

/// <summary>
/// Which kind of invoice an <c>InvoiceModel</c> is. Values are persisted; never renumber them.
/// </summary>
public enum InvoiceKind : byte
{
    /// <summary>
    /// A BOLT 11 invoice (the <c>createinvoice</c> string).
    /// </summary>
    Bolt11 = 0,

    /// <summary>
    /// A BOLT 12 invoice we issued in answer to an invoice_request for one of our offers.
    /// </summary>
    Bolt12 = 1
}