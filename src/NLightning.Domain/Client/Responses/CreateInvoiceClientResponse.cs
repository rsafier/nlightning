namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The invoice created by <c>CreateInvoice</c>; it is already persisted and payable.
/// </summary>
public sealed class CreateInvoiceClientResponse
{
    public InvoiceInfoClientResponse Invoice { get; }

    public CreateInvoiceClientResponse(InvoiceInfoClientResponse invoice)
    {
        Invoice = invoice;
    }
}