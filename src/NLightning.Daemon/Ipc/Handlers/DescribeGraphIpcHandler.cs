using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class DescribeGraphIpcHandler
    : ClientCommandIpcHandler<DescribeGraphIpcRequest, DescribeGraphClientRequest, DescribeGraphClientResponse, DescribeGraphIpcResponse>
{
    public override ClientCommand Command => ClientCommand.DescribeGraph;

    public DescribeGraphIpcHandler(ILogger<DescribeGraphIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override DescribeGraphClientRequest ToClientRequest(DescribeGraphIpcRequest request) =>
        request.ToClientRequest();

    protected override DescribeGraphIpcResponse ToIpcResponse(DescribeGraphClientResponse response) =>
        DescribeGraphIpcResponse.FromClientResponse(response);
}