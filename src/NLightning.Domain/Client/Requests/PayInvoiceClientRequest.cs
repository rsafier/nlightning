namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;
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

    /// <summary>
    /// The trampoline node to pay through (NL-875, <c>--trampoline</c>); null lets the node's
    /// <c>Node:Payments:Trampoline</c> decide.
    /// </summary>
    public CompactPubKey? TrampolineNode { get; init; }

    public PayInvoiceClientRequest(string bolt11)
    {
        Bolt11 = bolt11;
    }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, <c>--label</c>): stored on the row and copied into the accounting event's
    /// details; null for none. Checked by the daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// The operator's tags as <c>key=value</c> (NL-602 A3-T1, <c>--tag</c>, repeatable); empty for none. Checked by the
    /// daemon (<c>SourceLabelRules</c>).
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}