using System.Globalization;

namespace NLightning.Client.Printers;

using Domain.Payments.Enums;
using Transport.Ipc.Responses;

/// <summary>
/// The text layout shared by the invoice and payment printers. Output is culture-invariant, so it can be compared
/// byte for byte in tests and parsed by scripts.
/// </summary>
internal static class PaymentsPrintFormat
{
    internal const string Separator =
        "----------------------------------------------------------------------------------";

    internal static void WriteInvoice(TextWriter output, InvoiceInfoIpcResponse invoice)
    {
        output.WriteLine("  Bolt11:             {0}", invoice.Bolt11);
        output.WriteLine("  Payment Hash:       {0}", invoice.PaymentHash);
        output.WriteLine("  Amount (msat):      {0}",
                         invoice.Amount is null ? "any" : Invariant(invoice.Amount.MilliSatoshi));
        output.WriteLine("  Description:        {0}",
                         string.IsNullOrEmpty(invoice.Description) ? "-" : invoice.Description);
        output.WriteLine("  Status:             {0}",
                         invoice.IsExpired && invoice.Status == InvoiceStatus.Open
                             ? "Open (expired)"
                             : invoice.Status.ToString());
        output.WriteLine("  Created:            {0}", FormatTime(invoice.CreatedAt));
        output.WriteLine("  Expires:            {0}", FormatTime(invoice.ExpiresAt));
        if (invoice.AmountReceived is not null)
            output.WriteLine("  Received (msat):    {0}", Invariant(invoice.AmountReceived.MilliSatoshi));
        if (invoice.SettledAt is { } settledAt)
            output.WriteLine("  Settled:            {0}", FormatTime(settledAt));
    }

    internal static void WritePayment(TextWriter output, PaymentInfoIpcResponse payment)
    {
        output.WriteLine("  Payment Hash:       {0}", payment.PaymentHash);
        output.WriteLine("  Status:             {0}", payment.Status);
        output.WriteLine("  Payee:              {0}", payment.PayeeNodeId);
        output.WriteLine("  Amount (msat):      {0}", Invariant(payment.Amount.MilliSatoshi));
        output.WriteLine("  Fee (msat):         {0}", Invariant(payment.Fee.MilliSatoshi));
        if (payment.Preimage is { } preimage)
            output.WriteLine("  Preimage:           {0}", Convert.ToHexStringLower((byte[])preimage));
        if (payment.Status == PaymentStatus.Failed || payment.FailureCode is not null)
            output.WriteLine("  Failure:            {0}", FormatFailure(payment));
        if (!string.IsNullOrEmpty(payment.FailureReason))
            output.WriteLine("  Failure Reason:     {0}", payment.FailureReason);
        if (payment.OutgoingChannelId is { } channelId)
            output.WriteLine("  Outgoing HTLC:      {0} #{1}", channelId,
                             payment.OutgoingHtlcId is { } htlcId ? Invariant(htlcId) : "-");
        output.WriteLine("  Created:            {0}", FormatTime(payment.CreatedAt));
        if (payment.CompletedAt is { } completedAt)
            output.WriteLine("  Completed:          {0}", FormatTime(completedAt));
        if (!string.IsNullOrEmpty(payment.Bolt11))
            output.WriteLine("  Bolt11:             {0}", payment.Bolt11);
    }

    /// <summary>
    /// A BOLT 4 failure as <c>Name (0xCODE)</c> plus the failing hop, or "unknown" when the origin could not read it.
    /// </summary>
    internal static string FormatFailure(PaymentInfoIpcResponse payment)
    {
        if (payment.FailureCode is not { } code)
            return "unknown";

        var name = Enum.IsDefined(code) ? code.ToString() : "Unknown";
        var text = $"{name} (0x{(ushort)code:x4})";
        return payment.FailureSourceIndex is { } index
                   ? $"{text} at hop {index.ToString(CultureInfo.InvariantCulture)}"
                   : text;
    }

    internal static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Invariant(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}