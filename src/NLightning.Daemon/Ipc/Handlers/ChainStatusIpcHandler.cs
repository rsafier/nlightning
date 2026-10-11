using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ChainStatusIpcHandler
    : ClientCommandIpcHandler<ChainStatusIpcRequest, ChainStatusClientRequest, ChainStatusClientResponse,
        ChainStatusIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ChainStatus;

    public ChainStatusIpcHandler(ILogger<ChainStatusIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ChainStatusClientRequest ToClientRequest(ChainStatusIpcRequest request) =>
        request.ToClientRequest();

    protected override ChainStatusIpcResponse ToIpcResponse(ChainStatusClientResponse response) =>
        ChainStatusIpcResponse.FromClientResponse(response);
}