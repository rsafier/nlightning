using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class PayRouteIpcHandler
    : ClientCommandIpcHandler<PayRouteIpcRequest, PayRouteClientRequest, PayRouteClientResponse,
        PayRouteIpcResponse>
{
    public override ClientCommand Command => ClientCommand.PayRoute;

    public PayRouteIpcHandler(ILogger<PayRouteIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override PayRouteClientRequest ToClientRequest(PayRouteIpcRequest request) =>
        request.ToClientRequest();

    protected override PayRouteIpcResponse ToIpcResponse(PayRouteClientResponse response) =>
        PayRouteIpcResponse.FromClientResponse(response);
}