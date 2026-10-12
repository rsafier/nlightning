namespace NLightning.Domain.Payments.Models;

/// <summary>
/// The outcome of one <c>IPaymentService.PayInvoiceAsync</c> call with <see cref="PayInvoiceOptions"/>.
/// </summary>
/// <param name="Payment">The payment as stored when it completed or the wait ended (still <c>InFlight</c> then).</param>
/// <param name="Attempts">How many HTLCs this call offered (every part of every round).</param>
/// <param name="Parts">The most HTLCs this call had in flight at once (1 for an unsplit payment).</param>
public sealed record PayInvoiceResult(PaymentModel Payment, int Attempts, int Parts);