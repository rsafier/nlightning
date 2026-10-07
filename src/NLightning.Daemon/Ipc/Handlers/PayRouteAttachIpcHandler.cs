using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>ClientCommand 56 (<c>payroute --attach</c>, NL-1276).</summary>
internal sealed class PayRouteAttachIpcHandler
    : ClientCommandIpcHandler<PayRouteAttachIpcRequest, PayRouteAttachClientRequest, PayRouteClientResponse,
        PayRouteIpcResponse>
{
    public override ClientCommand Command => ClientCommand.PayRouteAttach;

    public PayRouteAttachIpcHandler(ILogger<PayRouteAttachIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override PayRouteAttachClientRequest ToClientRequest(PayRouteAttachIpcRequest request) =>
        request.ToClientRequest();

    protected override PayRouteIpcResponse ToIpcResponse(PayRouteClientResponse response) =>
        PayRouteIpcResponse.FromClientResponse(response);
}