using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class SetChannelPolicyIpcHandler
    : ClientCommandIpcHandler<SetChannelPolicyIpcRequest, SetChannelPolicyClientRequest, ChannelPolicyClientResponse,
        ChannelPolicyIpcResponse>
{
    public override ClientCommand Command => ClientCommand.SetChannelPolicy;

    public SetChannelPolicyIpcHandler(ILogger<SetChannelPolicyIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override SetChannelPolicyClientRequest ToClientRequest(SetChannelPolicyIpcRequest request) =>
        request.ToClientRequest();

    protected override ChannelPolicyIpcResponse ToIpcResponse(ChannelPolicyClientResponse response) =>
        ChannelPolicyIpcResponse.FromClientResponse(response);
}

internal sealed class GetChannelPolicyIpcHandler
    : ClientCommandIpcHandler<GetChannelPolicyIpcRequest, GetChannelPolicyClientRequest, ChannelPolicyClientResponse,
        ChannelPolicyIpcResponse>
{
    public override ClientCommand Command => ClientCommand.GetChannelPolicy;

    public GetChannelPolicyIpcHandler(ILogger<GetChannelPolicyIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override GetChannelPolicyClientRequest ToClientRequest(GetChannelPolicyIpcRequest request) =>
        request.ToClientRequest();

    protected override ChannelPolicyIpcResponse ToIpcResponse(ChannelPolicyClientResponse response) =>
        ChannelPolicyIpcResponse.FromClientResponse(response);
}