namespace NLightning.Client.Printers;

using Domain.Payments.Enums;
using Transport.Ipc.Responses;

public sealed class PayInvoicePrinter : IPrinter<PayInvoiceIpcResponse>
{
    private readonly TextWriter _output;

    public PayInvoicePrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(PayInvoiceIpcResponse item)
    {
        _output.WriteLine("Payment:");
        PaymentsPrintFormat.WritePayment(_output, item.Payment);
        if (item.Attempts > 0)
            _output.WriteLine($"  Attempts: {item.Attempts} HTLC(s), at most {item.Parts} in flight at once");
        if (item.Payment.Status == PaymentStatus.InFlight)
            _output.WriteLine("  The payment is still in flight; check it later with listpayments.");
    }
}