using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ListGraphChannelsIpcHandler
    : ClientCommandIpcHandler<ListGraphChannelsIpcRequest, ListGraphChannelsClientRequest, ListGraphChannelsClientResponse, ListGraphChannelsIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ListGraphChannels;

    public ListGraphChannelsIpcHandler(ILogger<ListGraphChannelsIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ListGraphChannelsClientRequest ToClientRequest(ListGraphChannelsIpcRequest request) => request.ToClientRequest();

    protected override ListGraphChannelsIpcResponse ToIpcResponse(ListGraphChannelsClientResponse response) =>
        ListGraphChannelsIpcResponse.FromClientResponse(response);
}