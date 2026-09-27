namespace NLightning.Domain.Payments.Models;

using Crypto.ValueObjects;
using Money;
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
                                       IReadOnlyList<BlindedPaymentPath> Paths, string? Invoice = null);