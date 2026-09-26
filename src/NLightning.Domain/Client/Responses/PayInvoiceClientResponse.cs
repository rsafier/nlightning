namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The outcome of <c>PayInvoice</c>: the payment as stored when it completed or the wait ended (then it is still
/// <c>InFlight</c>).
/// </summary>
public sealed class PayInvoiceClientResponse
{
    public PaymentInfoClientResponse Payment { get; }

    /// <summary>
    /// How many HTLCs the call offered (every part of every retry; 0 when no route was found).
    /// </summary>
    public int Attempts { get; init; }

    /// <summary>
    /// The most HTLCs the call had in flight at once (more than 1 for a split payment).
    /// </summary>
    public int Parts { get; init; }

    public PayInvoiceClientResponse(PaymentInfoClientResponse payment)
    {
        Payment = payment;
    }
}