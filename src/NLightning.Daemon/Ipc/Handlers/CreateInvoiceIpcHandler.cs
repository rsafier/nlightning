using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class CreateInvoiceIpcHandler
    : ClientCommandIpcHandler<CreateInvoiceIpcRequest, CreateInvoiceClientRequest, CreateInvoiceClientResponse,
        CreateInvoiceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.CreateInvoice;

    public CreateInvoiceIpcHandler(ILogger<CreateInvoiceIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override CreateInvoiceClientRequest ToClientRequest(CreateInvoiceIpcRequest request) =>
        request.ToClientRequest();

    protected override CreateInvoiceIpcResponse ToIpcResponse(CreateInvoiceClientResponse response) =>
        CreateInvoiceIpcResponse.FromClientResponse(response);
}