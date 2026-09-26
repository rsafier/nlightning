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

    public static ListPaymentsIpcResponse FromClientResponse(ListPaymentsClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ListPaymentsIpcResponse
        {
            Payments = clientResponse.Payments.Select(PaymentInfoIpcResponse.FromClientResponse).ToList()
        };
    }
}