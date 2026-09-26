namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class CreateInvoicePrinter : IPrinter<CreateInvoiceIpcResponse>
{
    private readonly TextWriter _output;

    public CreateInvoicePrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(CreateInvoiceIpcResponse item)
    {
        _output.WriteLine("Invoice:");
        PaymentsPrintFormat.WriteInvoice(_output, item.Invoice);
    }
}