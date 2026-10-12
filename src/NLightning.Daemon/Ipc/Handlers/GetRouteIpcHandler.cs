using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class GetRouteIpcHandler
    : ClientCommandIpcHandler<GetRouteIpcRequest, GetRouteClientRequest, GetRouteClientResponse, GetRouteIpcResponse>
{
    public override ClientCommand Command => ClientCommand.GetRoute;

    public GetRouteIpcHandler(ILogger<GetRouteIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override GetRouteClientRequest ToClientRequest(GetRouteIpcRequest request) => request.ToClientRequest();

    protected override GetRouteIpcResponse ToIpcResponse(GetRouteClientResponse response) =>
        GetRouteIpcResponse.FromClientResponse(response);
}