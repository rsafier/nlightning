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

    /// <summary>
    /// The most the payment may pay in routing fees, or null for the node's default (max(0.5 %, 5000 msat), NL-270).
    /// </summary>
    public LightningMoney? MaxFee { get; init; }

    /// <summary>
    /// The most HTLCs the payment may have in flight at once (1 never splits), or null for the node's default (16).
    /// </summary>
    public uint? MaxParts { get; init; }

    /// <summary>
    /// The only channel of ours the payment may leave through, as a channel id (64 hex characters) or a short channel id
    /// (<c>BLOCKxTXxOUTPUT</c>); null for any (NL-609).
    /// </summary>
    public string? OutgoingChannel { get; init; }

    /// <summary>
    /// For an invoice of our own (a circular rebalance, NL-609): the only channel of ours the payment may come back in
    /// through, as a channel id or a short channel id; null for any.
    /// </summary>
    public string? IncomingChannel { get; init; }

    public PayInvoiceClientRequest(string bolt11)
    {
        Bolt11 = bolt11;
    }
}