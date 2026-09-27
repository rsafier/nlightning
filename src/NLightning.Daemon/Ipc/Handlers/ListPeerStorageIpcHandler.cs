using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ListPeerStorageIpcHandler
    : ClientCommandIpcHandler<ListPeerStorageIpcRequest, ListPeerStorageClientRequest, ListPeerStorageClientResponse,
        ListPeerStorageIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ListPeerStorage;

    public ListPeerStorageIpcHandler(ILogger<ListPeerStorageIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ListPeerStorageClientRequest ToClientRequest(ListPeerStorageIpcRequest request) =>
        request.ToClientRequest();

    protected override ListPeerStorageIpcResponse ToIpcResponse(ListPeerStorageClientResponse response) =>
        ListPeerStorageIpcResponse.FromClientResponse(response);
}