namespace NLightning.Domain.Client.Responses;

/// <summary>
/// Our outgoing payments, newest first.
/// </summary>
public sealed class ListPaymentsClientResponse
{
    public IReadOnlyList<PaymentInfoClientResponse> Payments { get; }

    /// <summary>
    /// How many outgoing legs of trampoline relays the listing left out (NL-899): every one stored when the request
    /// did not ask for them, else 0.
    /// </summary>
    public int HiddenRelayLegs { get; }

    public ListPaymentsClientResponse(IReadOnlyList<PaymentInfoClientResponse> payments, int hiddenRelayLegs = 0)
    {
        Payments = payments;
        HiddenRelayLegs = hiddenRelayLegs;
    }
}