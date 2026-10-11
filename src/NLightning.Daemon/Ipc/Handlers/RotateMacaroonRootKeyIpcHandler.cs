using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class RotateMacaroonRootKeyIpcHandler
    : ClientCommandIpcHandler<RotateMacaroonRootKeyIpcRequest, RotateMacaroonRootKeyClientRequest,
        RotateMacaroonRootKeyClientResponse, RotateMacaroonRootKeyIpcResponse>
{
    public override ClientCommand Command => ClientCommand.RotateMacaroonRootKey;

    public RotateMacaroonRootKeyIpcHandler(ILogger<RotateMacaroonRootKeyIpcHandler> logger,
                                           IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override RotateMacaroonRootKeyClientRequest ToClientRequest(RotateMacaroonRootKeyIpcRequest request) =>
        request.ToClientRequest();

    protected override RotateMacaroonRootKeyIpcResponse ToIpcResponse(RotateMacaroonRootKeyClientResponse response) =>
        RotateMacaroonRootKeyIpcResponse.FromClientResponse(response);
}