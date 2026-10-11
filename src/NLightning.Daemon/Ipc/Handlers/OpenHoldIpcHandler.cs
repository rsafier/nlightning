using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class OpenHoldIpcHandler
    : ClientCommandIpcHandler<OpenHoldIpcRequest, OpenHoldClientRequest, OpenHoldClientResponse, OpenHoldIpcResponse>
{
    public override ClientCommand Command => ClientCommand.OpenHold;

    public OpenHoldIpcHandler(ILogger<OpenHoldIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override OpenHoldClientRequest ToClientRequest(OpenHoldIpcRequest request) => request.ToClientRequest();

    protected override OpenHoldIpcResponse ToIpcResponse(OpenHoldClientResponse response) =>
        OpenHoldIpcResponse.FromClientResponse(response);
}