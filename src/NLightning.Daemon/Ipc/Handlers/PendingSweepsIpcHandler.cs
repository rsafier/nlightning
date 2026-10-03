using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class PendingSweepsIpcHandler
    : ClientCommandIpcHandler<PendingSweepsIpcRequest, PendingSweepsClientRequest, PendingSweepsClientResponse,
        PendingSweepsIpcResponse>
{
    public override ClientCommand Command => ClientCommand.PendingSweeps;

    public PendingSweepsIpcHandler(ILogger<PendingSweepsIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override PendingSweepsClientRequest ToClientRequest(PendingSweepsIpcRequest request) =>
        request.ToClientRequest();

    protected override PendingSweepsIpcResponse ToIpcResponse(PendingSweepsClientResponse response) =>
        PendingSweepsIpcResponse.FromClientResponse(response);
}