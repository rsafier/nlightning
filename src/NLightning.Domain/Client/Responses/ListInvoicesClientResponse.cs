namespace NLightning.Domain.Client.Responses;

/// <summary>
/// Our invoices, newest first.
/// </summary>
public sealed class ListInvoicesClientResponse
{
    public IReadOnlyList<InvoiceInfoClientResponse> Invoices { get; }

    public ListInvoicesClientResponse(IReadOnlyList<InvoiceInfoClientResponse> invoices)
    {
        Invoices = invoices;
    }
}