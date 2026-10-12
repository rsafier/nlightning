using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>bumpopen</c> (ClientCommand 38, lane dfrbf) over IPC.
/// </summary>
internal sealed class BumpOpenIpcHandler
    : ClientCommandIpcHandler<BumpOpenIpcRequest, BumpOpenClientRequest, BumpOpenClientResponse, BumpOpenIpcResponse>
{
    public override ClientCommand Command => ClientCommand.BumpOpen;

    public BumpOpenIpcHandler(ILogger<BumpOpenIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override BumpOpenClientRequest ToClientRequest(BumpOpenIpcRequest request) => request.ToClientRequest();

    protected override BumpOpenIpcResponse ToIpcResponse(BumpOpenClientResponse response) =>
        BumpOpenIpcResponse.FromClientResponse(response);
}