using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for PayOffer (ClientCommand 29): the fetch and, when an invoice was received, the payment.
/// </summary>
[MessagePackObject]
public sealed class PayOfferIpcResponse
{
    [Key(0)] public required FetchInvoiceIpcResponse Fetch { get; init; }

    /// <summary>The payment, or null when no invoice was received.</summary>
    [Key(1)] public PaymentInfoIpcResponse? Payment { get; init; }

    [Key(2)] public int Attempts { get; init; }
    [Key(3)] public int Parts { get; init; }

    public static PayOfferIpcResponse FromClientResponse(PayOfferClientResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new PayOfferIpcResponse
        {
            Fetch = FetchInvoiceIpcResponse.FromClientResponse(response.Fetch),
            Payment = response.Payment is { } payment ? PaymentInfoIpcResponse.FromClientResponse(payment) : null,
            Attempts = response.Attempts,
            Parts = response.Parts
        };
    }
}