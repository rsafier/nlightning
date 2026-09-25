using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for PayInvoice (ClientCommand 10): the payment as stored when it completed or the wait ended (then it is
/// still <c>InFlight</c>).
/// </summary>
[MessagePackObject]
public sealed class PayInvoiceIpcResponse
{
    [Key(0)] public required PaymentInfoIpcResponse Payment { get; init; }

    public static PayInvoiceIpcResponse FromClientResponse(PayInvoiceClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new PayInvoiceIpcResponse
        {
            Payment = PaymentInfoIpcResponse.FromClientResponse(clientResponse.Payment)
        };
    }
}