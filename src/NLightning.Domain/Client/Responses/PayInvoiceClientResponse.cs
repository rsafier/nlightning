namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The outcome of <c>PayInvoice</c>: the payment as stored when it completed or the wait ended (then it is still
/// <c>InFlight</c>).
/// </summary>
public sealed class PayInvoiceClientResponse
{
    public PaymentInfoClientResponse Payment { get; }

    public PayInvoiceClientResponse(PaymentInfoClientResponse payment)
    {
        Payment = payment;
    }
}