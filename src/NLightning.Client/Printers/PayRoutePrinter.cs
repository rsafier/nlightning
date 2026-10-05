namespace NLightning.Client.Printers;

using System.Globalization;
using Domain.Payments.Enums;
using Transport.Ipc.Responses;

/// <summary>
/// Prints a <c>payroute</c> result (NL-1145): the payment as <c>payinvoice</c> reports it, then every supplied route's
/// outcome — its position in the request, its state, its HTLC and, when it failed, the attributed failure.
/// </summary>
public sealed class PayRoutePrinter : IPrinter<PayRouteIpcResponse>
{
    private readonly TextWriter _output;

    public PayRoutePrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(PayRouteIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine("Payment:");
        PaymentsPrintFormat.WritePayment(_output, item.Payment);
        if (item.Payment.Status == PaymentStatus.InFlight)
            _output.WriteLine("  The payment is still in flight; check it later with listpayments.");

        _output.WriteLine("Routes:");
        foreach (var outcome in item.RouteOutcomes)
            WriteOutcome(outcome);
    }

    private void WriteOutcome(RouteOutcomeIpcInfo outcome)
    {
        _output.WriteLine("  Route {0}:            {1}{2}", outcome.Index.ToString(CultureInfo.InvariantCulture),
                          outcome.Status, outcome.HtlcId is { } htlcId ? $"   HTLC {Invariant(htlcId)}" : string.Empty);
        if (outcome.Status != PaymentPartState.Failed)
            return;

        _output.WriteLine("    Failure:          {0}", FormatFailure(outcome));
        if (!string.IsNullOrEmpty(outcome.FailureReason))
            _output.WriteLine("    Failure Reason:   {0}", outcome.FailureReason);
    }

    /// <summary>
    /// A BOLT 4 failure as <c>Name (0xCODE)</c> plus the failing hop, or "unknown" when the origin could not read it —
    /// the payment's own format (<see cref="PaymentsPrintFormat.FormatFailure"/>).
    /// </summary>
    private static string FormatFailure(RouteOutcomeIpcInfo outcome)
    {
        if (outcome.FailureCode is not { } code)
            return "unknown";

        var name = Enum.IsDefined(code) ? code.ToString() : "Unknown";
        var text = $"{name} (0x{(ushort)code:x4})";
        return outcome.FailureSourceIndex is { } index
                   ? $"{text} at hop {index.ToString(CultureInfo.InvariantCulture)}"
                   : text;
    }

    private static string Invariant(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}