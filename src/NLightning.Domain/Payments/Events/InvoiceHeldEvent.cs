namespace NLightning.Domain.Payments.Events;

using Crypto.ValueObjects;
using Money;

/// <summary>
/// A hold invoice's paying HTLC set completed and is held (NL-995, Cashu plan C4): locked in, nothing fulfilled or
/// failed, waiting for the operator's settle or cancel — the moment a NUT-14 swap counterparty or an ASP learns its
/// side may proceed.
/// </summary>
public sealed record InvoiceHeldEvent(Hash PaymentHash, LightningMoney Amount, DateTimeOffset OccurredAt)
    : PaymentEvent(PaymentHash, OccurredAt);