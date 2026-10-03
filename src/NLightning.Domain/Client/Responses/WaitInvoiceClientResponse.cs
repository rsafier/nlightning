namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The invoice a <c>WaitInvoice</c> (46) waited on, as it was when the wait ended (Cashu plan C0, NL-812).
/// </summary>
public sealed class WaitInvoiceClientResponse
{
    public WaitInvoiceClientResponse(InvoiceInfoClientResponse invoice, bool timedOut)
    {
        Invoice = invoice;
        TimedOut = timedOut;
    }

    /// <summary>The invoice.</summary>
    public InvoiceInfoClientResponse Invoice { get; }

    /// <summary>True when the timeout ended the wait while the invoice was still <c>Open</c>.</summary>
    public bool TimedOut { get; }
}