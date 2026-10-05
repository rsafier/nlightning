using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

// The hold invoice commands (NL-995): one file for the three thin adapters, as for the offers and splices

internal sealed class CreateHoldInvoiceIpcHandler
    : ClientCommandIpcHandler<CreateHoldInvoiceIpcRequest, CreateHoldInvoiceClientRequest, HoldInvoiceClientResponse,
        HoldInvoiceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.CreateHoldInvoice;

    public CreateHoldInvoiceIpcHandler(ILogger<CreateHoldInvoiceIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override CreateHoldInvoiceClientRequest ToClientRequest(CreateHoldInvoiceIpcRequest request) =>
        request.ToClientRequest();

    protected override HoldInvoiceIpcResponse ToIpcResponse(HoldInvoiceClientResponse response) =>
        HoldInvoiceIpcResponse.FromClientResponse(response);
}

internal sealed class SettleHoldInvoiceIpcHandler
    : ClientCommandIpcHandler<SettleHoldInvoiceIpcRequest, SettleHoldInvoiceClientRequest, HoldInvoiceClientResponse,
        HoldInvoiceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.SettleHoldInvoice;

    public SettleHoldInvoiceIpcHandler(ILogger<SettleHoldInvoiceIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override SettleHoldInvoiceClientRequest ToClientRequest(SettleHoldInvoiceIpcRequest request) =>
        request.ToClientRequest();

    protected override HoldInvoiceIpcResponse ToIpcResponse(HoldInvoiceClientResponse response) =>
        HoldInvoiceIpcResponse.FromClientResponse(response);
}

internal sealed class CancelHoldInvoiceIpcHandler
    : ClientCommandIpcHandler<CancelHoldInvoiceIpcRequest, CancelHoldInvoiceClientRequest, HoldInvoiceClientResponse,
        HoldInvoiceIpcResponse>
{
    public override ClientCommand Command => ClientCommand.CancelHoldInvoice;

    public CancelHoldInvoiceIpcHandler(ILogger<CancelHoldInvoiceIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override CancelHoldInvoiceClientRequest ToClientRequest(CancelHoldInvoiceIpcRequest request) =>
        request.ToClientRequest();

    protected override HoldInvoiceIpcResponse ToIpcResponse(HoldInvoiceClientResponse response) =>
        HoldInvoiceIpcResponse.FromClientResponse(response);
}