namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The hold invoice after <c>CreateHoldInvoice</c>, <c>SettleHoldInvoice</c> or <c>CancelHoldInvoice</c> (NL-995):
/// created (<c>Open</c>), settled (<c>Settled</c>, with its settlement time) or canceled (<c>Canceled</c>). Like every
/// invoice response it never carries the preimage.
/// </summary>
public sealed class HoldInvoiceClientResponse
{
    public InvoiceInfoClientResponse Invoice { get; }

    public HoldInvoiceClientResponse(InvoiceInfoClientResponse invoice)
    {
        Invoice = invoice;
    }
}