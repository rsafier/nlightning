namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ListInvoicesPrinter : IPrinter<ListInvoicesIpcResponse>
{
    private readonly TextWriter _output;

    public ListInvoicesPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListInvoicesIpcResponse item)
    {
        _output.WriteLine("Invoices:");
        if (item.Invoices.Count == 0)
        {
            _output.WriteLine("  None");
            return;
        }

        _output.WriteLine(PaymentsPrintFormat.Separator);
        foreach (var invoice in item.Invoices)
        {
            PaymentsPrintFormat.WriteInvoice(_output, invoice);
            _output.WriteLine(PaymentsPrintFormat.Separator);
        }
    }
}