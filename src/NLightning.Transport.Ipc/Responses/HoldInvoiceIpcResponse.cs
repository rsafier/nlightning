using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for CreateHoldInvoice, SettleHoldInvoice and CancelHoldInvoice (ClientCommand 49-51, NL-995): the hold
/// invoice after the command, already persisted.
/// </summary>
[MessagePackObject]
public sealed class HoldInvoiceIpcResponse
{
    [Key(0)] public required InvoiceInfoIpcResponse Invoice { get; init; }

    public static HoldInvoiceIpcResponse FromClientResponse(HoldInvoiceClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new HoldInvoiceIpcResponse
        {
            Invoice = InvoiceInfoIpcResponse.FromClientResponse(clientResponse.Invoice)
        };
    }
}