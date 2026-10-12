namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints a hold invoice verb's answer (NL-995): the invoice, in the same layout createinvoice prints (see
/// <see cref="CreateInvoicePrinter"/>).
/// </summary>
public sealed class HoldInvoicePrinter : IPrinter<HoldInvoiceIpcResponse>
{
    private readonly TextWriter _output;

    public HoldInvoicePrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(HoldInvoiceIpcResponse item)
    {
        _output.WriteLine("Invoice:");
        PaymentsPrintFormat.WriteInvoice(_output, item.Invoice);
    }
}