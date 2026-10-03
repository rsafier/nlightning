namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ListPaymentsPrinter : IPrinter<ListPaymentsIpcResponse>
{
    private readonly TextWriter _output;

    public ListPaymentsPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListPaymentsIpcResponse item)
    {
        _output.WriteLine("Payments:");
        if (item.Payments.Count == 0)
        {
            _output.WriteLine("  None");
            return;
        }

        _output.WriteLine(PaymentsPrintFormat.Separator);
        foreach (var payment in item.Payments)
        {
            PaymentsPrintFormat.WritePayment(_output, payment);
            _output.WriteLine(PaymentsPrintFormat.Separator);
        }
    }
}