using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ForceCloseChannelIpcHandler
    : ClientCommandIpcHandler<ForceCloseChannelIpcRequest, ForceCloseChannelClientRequest,
        ForceCloseChannelClientResponse, ForceCloseChannelIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ForceCloseChannel;

    public ForceCloseChannelIpcHandler(ILogger<ForceCloseChannelIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ForceCloseChannelClientRequest ToClientRequest(ForceCloseChannelIpcRequest request) =>
        request.ToClientRequest();

    protected override ForceCloseChannelIpcResponse ToIpcResponse(ForceCloseChannelClientResponse response) =>
        ForceCloseChannelIpcResponse.FromClientResponse(response);
}