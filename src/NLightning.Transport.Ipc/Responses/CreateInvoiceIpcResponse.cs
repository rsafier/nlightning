using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for CreateInvoice (ClientCommand 9): the invoice, already persisted and payable.
/// </summary>
[MessagePackObject]
public sealed class CreateInvoiceIpcResponse
{
    [Key(0)] public required InvoiceInfoIpcResponse Invoice { get; init; }

    public static CreateInvoiceIpcResponse FromClientResponse(CreateInvoiceClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new CreateInvoiceIpcResponse
        {
            Invoice = InvoiceInfoIpcResponse.FromClientResponse(clientResponse.Invoice)
        };
    }
}