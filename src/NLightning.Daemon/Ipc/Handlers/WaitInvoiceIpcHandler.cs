using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class WaitInvoiceIpcHandler
    : ClientCommandIpcHandler<WaitInvoiceIpcRequest, WaitInvoiceClientRequest, WaitInvoiceClientResponse,
        WaitInvoiceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.WaitInvoice;

    public WaitInvoiceIpcHandler(ILogger<WaitInvoiceIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override WaitInvoiceClientRequest ToClientRequest(WaitInvoiceIpcRequest request) =>
        request.ToClientRequest();

    protected override WaitInvoiceIpcResponse ToIpcResponse(WaitInvoiceClientResponse response) =>
        WaitInvoiceIpcResponse.FromClientResponse(response);
}