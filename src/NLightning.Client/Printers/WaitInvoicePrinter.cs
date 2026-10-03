namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints a <c>waitinvoice</c> answer (ClientCommand 47, NL-901): the invoice, and whether the wait timed out.
/// </summary>
public sealed class WaitInvoicePrinter : IPrinter<WaitInvoiceIpcResponse>
{
    private readonly TextWriter _output;

    public WaitInvoicePrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(WaitInvoiceIpcResponse item)
    {
        _output.WriteLine(item.TimedOut ? "Timed out; the invoice is still open:" : "Invoice:");
        _output.WriteLine(PaymentsPrintFormat.Separator);
        PaymentsPrintFormat.WriteInvoice(_output, item.Invoice);
        _output.WriteLine(PaymentsPrintFormat.Separator);
    }
}