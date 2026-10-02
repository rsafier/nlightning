namespace NLightning.Application.Payments;

using Domain.Crypto.ValueObjects;
using Domain.Payments.Enums;
using Domain.Payments.Models;

/// <summary>
/// The one rule both halves of a rebalance use to flag themselves <c>selfPayment</c> (NL-609, NL-670): our payment
/// paid one of our own BOLT 11 invoices, so <b>we are the payee</b>. The paying side (the <c>PaymentSucceeded</c>) and
/// the receiving side (the <c>InvoiceSettled</c>) apply the same test, so the books' rebalance account is never left
/// one-sided.
/// </summary>
/// <remarks>
/// A payment hash alone proves nothing: a peer that learned the preimage of an invoice of ours can issue its own
/// invoice on the same hash (we pay it: an ordinary payment), and a keysend received on the hash of a payment we made
/// is ordinary income. Neither has our node as the payment's payee, and a keysend record is never an invoice of ours.
/// </remarks>
public static class SelfPaymentRule
{
    /// <summary>
    /// Whether <paramref name="payment"/> pays <paramref name="invoice"/> as a self-payment: same hash, the invoice is a
    /// BOLT 11 invoice of ours (not a keysend record, not a BOLT 12 invoice), the payment is not a keysend and its payee
    /// is <paramref name="ourNodeId"/>. False when any of them is unknown. The payment's status is the caller's check.
    /// </summary>
    public static bool IsSelfPayment(PaymentModel? payment, InvoiceModel? invoice, CompactPubKey? ourNodeId)
    {
        if (payment is null || invoice is null || ourNodeId is not { } us)
            return false;

        return payment.PaymentHash == invoice.PaymentHash
            && invoice is { Bolt11: not null, Keysend: null, Bolt12: null }
            && payment.Keysend is null
            && payment.PayeeNodeId == us;
    }

    /// <summary>
    /// The receiving side's test when <paramref name="invoice"/> settles: one of our own payments of it is in flight or
    /// succeeded (<paramref name="payment"/>, the payment row of the invoice's hash) and
    /// <see cref="IsSelfPayment"/> holds.
    /// </summary>
    public static bool IsSettledBySelfPayment(InvoiceModel invoice, PaymentModel? payment, CompactPubKey? ourNodeId) =>
        payment is { Status: PaymentStatus.InFlight or PaymentStatus.Succeeded }
     && IsSelfPayment(payment, invoice, ourNodeId);
}