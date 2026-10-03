namespace NLightning.Domain.Client.Requests;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// Fetches an invoice for a BOLT 12 offer and pays it (<c>ClientCommand.PayOffer</c>), or only fetches it
/// (<c>ClientCommand.FetchInvoice</c>, which ignores the payment limits).
/// </summary>
public sealed class PayOfferClientRequest
{
    public PayOfferClientRequest(string offer)
    {
        Offer = offer;
    }

    /// <summary>The <c>lno1...</c> string.</summary>
    public string Offer { get; }

    /// <summary><c>invreq_amount</c>: required when the offer has no amount or is in another currency.</summary>
    public LightningMoney? Amount { get; init; }

    /// <summary><c>invreq_quantity</c>: required when the offer has <c>offer_quantity_max</c>.</summary>
    public ulong? Quantity { get; init; }

    /// <summary><c>invreq_payer_note</c>.</summary>
    public string? PayerNote { get; init; }

    /// <summary>How long to wait for the payment's outcome, in seconds, or null for the default (60).</summary>
    public uint? TimeoutSeconds { get; init; }

    /// <summary>The most the payment may pay in fees (the blinded paths' fees included), or null for the default.</summary>
    public LightningMoney? MaxFee { get; init; }

    /// <summary>The most HTLCs in flight at once, or null for the default.</summary>
    public uint? MaxParts { get; init; }

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

    /// <summary>
    /// The trampoline node to pay through (NL-875, <c>--trampoline</c>); null lets the node's
    /// <c>Node:Payments:Trampoline</c> decide.
    /// </summary>
    public CompactPubKey? TrampolineNode { get; init; }
}