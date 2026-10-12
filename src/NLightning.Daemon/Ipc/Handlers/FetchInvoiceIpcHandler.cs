using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class FetchInvoiceIpcHandler
    : ClientCommandIpcHandler<PayOfferIpcRequest, PayOfferClientRequest, FetchInvoiceClientResponse,
        FetchInvoiceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.FetchInvoice;

    public FetchInvoiceIpcHandler(ILogger<FetchInvoiceIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override PayOfferClientRequest ToClientRequest(PayOfferIpcRequest request) => request.ToClientRequest();

    protected override FetchInvoiceIpcResponse ToIpcResponse(FetchInvoiceClientResponse response) =>
        FetchInvoiceIpcResponse.FromClientResponse(response);
}