using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for ListInvoices (ClientCommand 11): our invoices, newest first.
/// </summary>
[MessagePackObject]
public sealed class ListInvoicesIpcResponse
{
    [Key(0)] public required List<InvoiceInfoIpcResponse> Invoices { get; init; }

    public static ListInvoicesIpcResponse FromClientResponse(ListInvoicesClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new ListInvoicesIpcResponse
        {
            Invoices = clientResponse.Invoices.Select(InvoiceInfoIpcResponse.FromClientResponse).ToList()
        };
    }
}