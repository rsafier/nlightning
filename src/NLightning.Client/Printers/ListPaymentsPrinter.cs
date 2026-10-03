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
        }
        else
        {
            _output.WriteLine(PaymentsPrintFormat.Separator);
            foreach (var payment in item.Payments)
            {
                PaymentsPrintFormat.WritePayment(_output, payment);
                _output.WriteLine(PaymentsPrintFormat.Separator);
            }
        }

        // NL-899: the relays' outgoing legs are not our spending; say they were left out and how to see them
        if (item.HiddenRelayLegs > 0)
            _output.WriteLine("  {0} outgoing leg(s) of trampoline relays not shown (--include-relay-legs lists "
                            + "them; listforwards lists the relays)",
                              item.HiddenRelayLegs.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}