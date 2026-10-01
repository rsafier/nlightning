using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// The text layout of the BOLT 12 offer commands (culture-invariant, times as in the invoice printers).
/// </summary>
internal static class OfferPrintFormat
{
    internal static void WriteOffer(TextWriter output, OfferInfoIpcResponse offer)
    {
        output.WriteLine("  Offer:              {0}", offer.Bolt12);
        output.WriteLine("  Offer Id:           {0}", offer.OfferId);
        output.WriteLine("  Amount (msat):      {0}",
                         offer.Amount is null ? "any" : Invariant(offer.Amount.MilliSatoshi));
        output.WriteLine("  Description:        {0}",
                         string.IsNullOrEmpty(offer.Description) ? "-" : offer.Description);
        if (!string.IsNullOrEmpty(offer.Issuer))
            output.WriteLine("  Issuer:             {0}", offer.Issuer);
        if (offer.QuantityMax is { } quantityMax)
            output.WriteLine("  Quantity Max:       {0}", quantityMax == 0 ? "unlimited" : Invariant(quantityMax));
        output.WriteLine("  Status:             {0}",
                         offer.IsActive || offer.Status != Domain.Offers.Enums.OfferStatus.Active
                             ? offer.Status.ToString()
                             : "Active (expired)");
        output.WriteLine("  Blinded Paths:      {0}", offer.HasPaths ? "yes" : "no");
        output.WriteLine("  Created:            {0}", PaymentsPrintFormat.FormatTime(offer.CreatedAt));
        if (offer.AbsoluteExpiry is { } expiry)
            output.WriteLine("  Expires:            {0}", PaymentsPrintFormat.FormatTime(expiry));
        if (offer.DisabledAt is { } disabledAt)
            output.WriteLine("  Disabled:           {0}", PaymentsPrintFormat.FormatTime(disabledAt));
        if (offer.PaidInvoices is { } paid)
            output.WriteLine("  Invoices:           {0} paid, {1} unpaid", Invariant((ulong)paid),
                             Invariant((ulong)(offer.UnpaidInvoices ?? 0)));
    }

    private static string Invariant(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed class CreateOfferPrinter : IPrinter<CreateOfferIpcResponse>
{
    private readonly TextWriter _output;

    public CreateOfferPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(CreateOfferIpcResponse item)
    {
        _output.WriteLine("Offer:");
        OfferPrintFormat.WriteOffer(_output, item.Offer);
        if (!string.IsNullOrEmpty(item.Warning))
            _output.WriteLine("  Warning: {0}", item.Warning);
    }
}

public sealed class ListOffersPrinter : IPrinter<ListOffersIpcResponse>
{
    private readonly TextWriter _output;

    public ListOffersPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListOffersIpcResponse item)
    {
        _output.WriteLine("Offers:");
        if (item.Offers.Count == 0)
        {
            _output.WriteLine("  None");
            return;
        }

        _output.WriteLine(PaymentsPrintFormat.Separator);
        foreach (var offer in item.Offers)
        {
            OfferPrintFormat.WriteOffer(_output, offer);
            _output.WriteLine(PaymentsPrintFormat.Separator);
        }
    }
}

public sealed class DisableOfferPrinter : IPrinter<DisableOfferIpcResponse>
{
    private readonly TextWriter _output;

    public DisableOfferPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(DisableOfferIpcResponse item)
    {
        _output.WriteLine(item.Changed ? "Offer disabled:" : "Offer unchanged (it no longer answered):");
        OfferPrintFormat.WriteOffer(_output, item.Offer);
    }
}