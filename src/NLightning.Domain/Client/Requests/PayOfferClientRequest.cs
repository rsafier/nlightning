namespace NLightning.Domain.Client.Requests;

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
}