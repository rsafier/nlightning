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

    /// <summary>
    /// How many HTLCs the call offered (every part of every retry).
    /// </summary>
    [Key(1)] public int Attempts { get; init; }

    /// <summary>
    /// The most HTLCs the call had in flight at once (more than 1 for a split payment).
    /// </summary>
    [Key(2)] public int Parts { get; init; }

    public static PayInvoiceIpcResponse FromClientResponse(PayInvoiceClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new PayInvoiceIpcResponse
        {
            Payment = PaymentInfoIpcResponse.FromClientResponse(clientResponse.Payment),
            Attempts = clientResponse.Attempts,
            Parts = clientResponse.Parts
        };
    }
}