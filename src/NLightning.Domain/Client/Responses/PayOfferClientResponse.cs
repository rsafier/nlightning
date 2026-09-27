namespace NLightning.Domain.Client.Responses;

/// <summary>
/// The outcome of <c>payoffer</c>: how the invoice was fetched and, when one was, the payment as stored when it
/// completed or the wait ended.
/// </summary>
public sealed class PayOfferClientResponse
{
    public PayOfferClientResponse(FetchInvoiceClientResponse fetch, PaymentInfoClientResponse? payment)
    {
        Fetch = fetch;
        Payment = payment;
    }

    public FetchInvoiceClientResponse Fetch { get; }

    /// <summary>The payment, or null when no invoice was received (nothing was paid).</summary>
    public PaymentInfoClientResponse? Payment { get; }

    /// <summary>How many HTLCs the payment offered.</summary>
    public int Attempts { get; init; }

    /// <summary>The most HTLCs the payment had in flight at once.</summary>
    public int Parts { get; init; }
}