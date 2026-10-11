using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class DisconnectPeerIpcHandler
    : ClientCommandIpcHandler<DisconnectPeerIpcRequest, DisconnectPeerClientRequest, DisconnectPeerClientResponse,
        DisconnectPeerIpcResponse>
{
    public override ClientCommand Command => ClientCommand.DisconnectPeer;

    public DisconnectPeerIpcHandler(ILogger<DisconnectPeerIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override DisconnectPeerClientRequest ToClientRequest(DisconnectPeerIpcRequest request) =>
        request.ToClientRequest();

    protected override DisconnectPeerIpcResponse ToIpcResponse(DisconnectPeerClientResponse response) =>
        DisconnectPeerIpcResponse.FromClientResponse(response);
}