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
    Bolt12 = 1,

    /// <summary>
    /// A spontaneous (keysend) payment we received: no invoice was issued, the payer chose the preimage and sent it in
    /// the final hop's <c>keysend_preimage</c> record. The record is created when the HTLC is accepted, for accounting.
    /// </summary>
    Keysend = 2
}