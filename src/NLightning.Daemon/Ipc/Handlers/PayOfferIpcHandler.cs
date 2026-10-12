using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class PayOfferIpcHandler
    : ClientCommandIpcHandler<PayOfferIpcRequest, PayOfferClientRequest, PayOfferClientResponse, PayOfferIpcResponse>
{
    public override ClientCommand Command => ClientCommand.PayOffer;

    public PayOfferIpcHandler(ILogger<PayOfferIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override PayOfferClientRequest ToClientRequest(PayOfferIpcRequest request) => request.ToClientRequest();

    protected override PayOfferIpcResponse ToIpcResponse(PayOfferClientResponse response) =>
        PayOfferIpcResponse.FromClientResponse(response);
}