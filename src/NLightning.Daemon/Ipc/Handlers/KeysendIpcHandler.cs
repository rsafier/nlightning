using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class KeysendIpcHandler
    : ClientCommandIpcHandler<KeysendIpcRequest, KeysendClientRequest, PayInvoiceClientResponse, PayInvoiceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.Keysend;

    public KeysendIpcHandler(ILogger<KeysendIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override KeysendClientRequest ToClientRequest(KeysendIpcRequest request) => request.ToClientRequest();

    protected override PayInvoiceIpcResponse ToIpcResponse(PayInvoiceClientResponse response) =>
        PayInvoiceIpcResponse.FromClientResponse(response);
}