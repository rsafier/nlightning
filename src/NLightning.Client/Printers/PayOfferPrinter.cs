using System.Globalization;

namespace NLightning.Client.Printers;

using Domain.Offers.Enums;
using Domain.Payments.Enums;
using Transport.Ipc.Responses;

/// <summary>
/// Prints <c>payoffer</c> (the fetch, then the payment) and <c>fetchinvoice</c> (the fetch only).
/// </summary>
public sealed class PayOfferPrinter : IPrinter<PayOfferIpcResponse>
{
    private readonly TextWriter _output;

    public PayOfferPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(PayOfferIpcResponse item)
    {
        PrintFetch(item.Fetch);
        if (item.Payment is not { } payment)
        {
            _output.WriteLine("Nothing was paid.");
            return;
        }

        _output.WriteLine("Payment:");
        PaymentsPrintFormat.WritePayment(_output, payment);
        if (item.Attempts > 0)
            _output.WriteLine($"  Attempts: {item.Attempts} HTLC(s), at most {item.Parts} in flight at once");
        if (payment.Status == PaymentStatus.InFlight)
            _output.WriteLine("  The payment is still in flight; check it later with listpayments.");
    }

    public void PrintFetch(FetchInvoiceIpcResponse fetch)
    {
        _output.WriteLine("Invoice request:");
        _output.WriteLine($"  Status: {fetch.Status}");
        _output.WriteLine($"  Requests answered or timed out: {fetch.Attempts}");
        if (fetch.Error is not null)
            _output.WriteLine(fetch.Status == FetchInvoiceStatus.InvoiceError
                                  ? $"  invoice_error: {fetch.Error}"
                                  : $"  Reason: {fetch.Error}");
        if (fetch.ErroneousField is { } field)
            _output.WriteLine($"  Erroneous field: {field.ToString(CultureInfo.InvariantCulture)}");
        if (fetch.Status != FetchInvoiceStatus.Received)
            return;

        _output.WriteLine("Invoice:");
        _output.WriteLine($"  Node id: {fetch.NodeId}");
        _output.WriteLine($"  Amount: {fetch.Amount?.MilliSatoshi.ToString(CultureInfo.InvariantCulture)} msat");
        _output.WriteLine($"  Payment hash: {fetch.PaymentHash}");
        if (fetch.CreatedAt is { } createdAt)
            _output.WriteLine($"  Created: {PaymentsPrintFormat.FormatTime(createdAt)}");
        if (fetch.ExpiresAt is { } expiresAt)
            _output.WriteLine($"  Expires: {PaymentsPrintFormat.FormatTime(expiresAt)}");
        _output.WriteLine($"  Blinded paths: {fetch.PathCount.ToString(CultureInfo.InvariantCulture)}");
        if (fetch.Invoice is { } invoice)
            _output.WriteLine($"  Invoice (hex): {Convert.ToHexString(invoice).ToLowerInvariant()}");
    }
}