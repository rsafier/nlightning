using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ShutdownIpcHandler
    : ClientCommandIpcHandler<ShutdownIpcRequest, ShutdownClientRequest, ShutdownClientResponse, ShutdownIpcResponse>
{
    public override ClientCommand Command => ClientCommand.Shutdown;

    public ShutdownIpcHandler(ILogger<ShutdownIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ShutdownClientRequest ToClientRequest(ShutdownIpcRequest request) => request.ToClientRequest();

    protected override ShutdownIpcResponse ToIpcResponse(ShutdownClientResponse response) =>
        ShutdownIpcResponse.FromClientResponse(response);
}