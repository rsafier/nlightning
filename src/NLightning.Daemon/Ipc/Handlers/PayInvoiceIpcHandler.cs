using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class PayInvoiceIpcHandler
    : ClientCommandIpcHandler<PayInvoiceIpcRequest, PayInvoiceClientRequest, PayInvoiceClientResponse,
        PayInvoiceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.PayInvoice;

    public PayInvoiceIpcHandler(ILogger<PayInvoiceIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override PayInvoiceClientRequest ToClientRequest(PayInvoiceIpcRequest request) =>
        request.ToClientRequest();

    protected override PayInvoiceIpcResponse ToIpcResponse(PayInvoiceClientResponse response) =>
        PayInvoiceIpcResponse.FromClientResponse(response);
}