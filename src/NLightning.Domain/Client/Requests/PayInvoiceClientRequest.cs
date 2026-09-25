namespace NLightning.Domain.Client.Requests;

using Money;

/// <summary>
/// Pays a BOLT 11 invoice (<c>ClientCommand.PayInvoice</c>).
/// </summary>
public sealed class PayInvoiceClientRequest
{
    public string Bolt11 { get; }

    /// <summary>
    /// The amount to pay when the invoice has none; leave null when it has one.
    /// </summary>
    public LightningMoney? Amount { get; init; }

    /// <summary>
    /// How long to wait for the outcome, in seconds, or null for the default (60). The payment keeps going after the
    /// wait ends; the response then reports it in flight.
    /// </summary>
    public uint? TimeoutSeconds { get; init; }

    public PayInvoiceClientRequest(string bolt11)
    {
        Bolt11 = bolt11;
    }
}