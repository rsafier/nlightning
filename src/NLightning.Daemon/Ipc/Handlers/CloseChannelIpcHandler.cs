using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class CloseChannelIpcHandler
    : ClientCommandIpcHandler<CloseChannelIpcRequest, CloseChannelClientRequest, CloseChannelClientResponse,
        CloseChannelIpcResponse>
{
    public override ClientCommand Command => ClientCommand.CloseChannel;

    public CloseChannelIpcHandler(ILogger<CloseChannelIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override CloseChannelClientRequest ToClientRequest(CloseChannelIpcRequest request) =>
        request.ToClientRequest();

    protected override CloseChannelIpcResponse ToIpcResponse(CloseChannelClientResponse response) =>
        CloseChannelIpcResponse.FromClientResponse(response);
}