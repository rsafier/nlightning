using MessagePack;

namespace NLightning.Transport.Ipc.Responses;

using Domain.Client.Responses;

/// <summary>
/// Response for WaitInvoice (ClientCommand 47, Cashu plan C0, NL-901): the invoice when the wait ended.
/// </summary>
[MessagePackObject]
public sealed class WaitInvoiceIpcResponse
{
    [Key(0)] public required InvoiceInfoIpcResponse Invoice { get; init; }

    /// <summary>True when the timeout ended the wait while the invoice was still open.</summary>
    [Key(1)] public bool TimedOut { get; init; }

    public static WaitInvoiceIpcResponse FromClientResponse(WaitInvoiceClientResponse clientResponse)
    {
        ArgumentNullException.ThrowIfNull(clientResponse);
        return new WaitInvoiceIpcResponse
        {
            Invoice = InvoiceInfoIpcResponse.FromClientResponse(clientResponse.Invoice),
            TimedOut = clientResponse.TimedOut
        };
    }
}