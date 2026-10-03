namespace NLightning.Domain.Client.Responses;

/// <summary>
/// Our outgoing payments, newest first.
/// </summary>
public sealed class ListPaymentsClientResponse
{
    public IReadOnlyList<PaymentInfoClientResponse> Payments { get; }

    public ListPaymentsClientResponse(IReadOnlyList<PaymentInfoClientResponse> payments)
    {
        Payments = payments;
    }
}