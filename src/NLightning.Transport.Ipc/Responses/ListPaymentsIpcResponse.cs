using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for ListPayments (ClientCommand 12): our outgoing payments, newest first.
/// </summary>
[MessagePackObject]
public sealed class ListPaymentsIpcResponse
{
    [Key(0)] public required List<PaymentInfoIpcResponse> Payments { get; init; }

    /// <summary>
    /// How many outgoing legs of trampoline relays the listing left out (NL-899); 0 when they were asked for, or from
    /// a daemon before NL-899.
    /// </summary>
    [Key(1)] public int HiddenRelayLegs { get; init; }

    public static ListPaymentsIpcResponse FromClientResponse(ListPaymentsClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ListPaymentsIpcResponse
        {
            Payments = clientResponse.Payments.Select(PaymentInfoIpcResponse.FromClientResponse).ToList(),
            HiddenRelayLegs = clientResponse.HiddenRelayLegs
        };
    }
}