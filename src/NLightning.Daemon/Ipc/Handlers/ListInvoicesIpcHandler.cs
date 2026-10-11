using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ListInvoicesIpcHandler
    : ClientCommandIpcHandler<ListInvoicesIpcRequest, ListInvoicesClientRequest, ListInvoicesClientResponse,
        ListInvoicesIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ListInvoices;

    public ListInvoicesIpcHandler(ILogger<ListInvoicesIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ListInvoicesClientRequest ToClientRequest(ListInvoicesIpcRequest request) =>
        request.ToClientRequest();

    protected override ListInvoicesIpcResponse ToIpcResponse(ListInvoicesClientResponse response) =>
        ListInvoicesIpcResponse.FromClientResponse(response);
}