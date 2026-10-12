using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ListNodesIpcHandler
    : ClientCommandIpcHandler<ListNodesIpcRequest, ListNodesClientRequest, ListNodesClientResponse, ListNodesIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ListNodes;

    public ListNodesIpcHandler(ILogger<ListNodesIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ListNodesClientRequest ToClientRequest(ListNodesIpcRequest request) => request.ToClientRequest();

    protected override ListNodesIpcResponse ToIpcResponse(ListNodesClientResponse response) =>
        ListNodesIpcResponse.FromClientResponse(response);
}