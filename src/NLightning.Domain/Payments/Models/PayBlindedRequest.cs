namespace NLightning.Domain.Payments.Models;

using Crypto.ValueObjects;
using Money;
using Offers.Models;
using Protocol.Onion.Models;

/// <summary>
/// A payment to a recipient that hides behind blinded paths (BOLT 4 "Route Blinding", sender side; ONION M5).
/// </summary>
/// <param name="PaymentHash">The HTLC's <c>payment_hash</c>.</param>
/// <param name="Amount">What the recipient receives (the final hop's <c>amt_to_forward</c> and
/// <c>total_amount_msat</c>).</param>
/// <param name="Paths">The recipient's blinded paths, in its order of preference; the cheapest usable one is taken.
/// </param>
/// <param name="Invoice">The invoice the paths come from, stored with the payment (optional).</param>
public sealed record PayBlindedRequest(Hash PaymentHash, LightningMoney Amount,
                                       IReadOnlyList<BlindedPaymentPath> Paths, string? Invoice = null)
{
    /// <summary>
    /// The recipient's real node id to store as the payment's payee (a BOLT 12 <c>invoice_node_id</c>), or null for
    /// the last blinded node id of the first path.
    /// </summary>
    public CompactPubKey? PayeeNodeId { get; init; }

    /// <summary>
    /// Whether the recipient accepts a payment split over several paths (BOLT 12 "Invoices" reader: MUST pay over
    /// several paths when <c>invoice_features</c> sets <c>basic_mpp</c> compulsory, MUST NOT split without the bit).
    /// False sends one HTLC.
    /// </summary>
    public bool AllowMpp { get; init; }

    /// <summary>
    /// The BOLT 12 offer and invoice the payment is for, stored with the payment (<c>PaymentModel.Bolt12</c>), or null.
    /// </summary>
    public Bolt12PaymentDetails? Bolt12 { get; init; }
}