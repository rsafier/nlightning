namespace NLightning.Domain.Payments.Events;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// A payment outcome raised after the save that made it true has committed (Cashu plan C0, NL-901): a subscriber that
/// reads the database when it gets one sees the new state.
/// </summary>
/// <param name="PaymentHash">The payment hash of the invoice or payment.</param>
/// <param name="OccurredAt">When the outcome was recorded.</param>
public abstract record PaymentEvent(Hash PaymentHash, DateTimeOffset OccurredAt);

/// <summary>
/// One of our invoices was settled (every part of its HTLC set fulfilled or carrying the preimage).
/// </summary>
/// <param name="Amount">What the settling HTLC set carried.</param>
public sealed record InvoiceSettledEvent(Hash PaymentHash, LightningMoney Amount, DateTimeOffset OccurredAt)
    : PaymentEvent(PaymentHash, OccurredAt);

/// <summary>
/// One of our payments succeeded.
/// </summary>
/// <param name="Amount">The amount delivered to the payee.</param>
/// <param name="Fee">The routing fee paid.</param>
/// <param name="Preimage">The payment preimage, the proof of payment.</param>
public sealed record PaymentSucceededEvent(Hash PaymentHash, LightningMoney Amount, LightningMoney Fee,
                                           Secret Preimage, DateTimeOffset OccurredAt)
    : PaymentEvent(PaymentHash, OccurredAt);

/// <summary>
/// One of our payments failed for good (no part of it is in flight any more).
/// </summary>
/// <param name="Reason">The stored failure reason, when there is one.</param>
public sealed record PaymentFailedEvent(Hash PaymentHash, string? Reason, DateTimeOffset OccurredAt)
    : PaymentEvent(PaymentHash, OccurredAt);