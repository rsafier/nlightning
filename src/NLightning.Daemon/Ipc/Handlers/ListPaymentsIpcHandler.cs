using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

internal sealed class ListPaymentsIpcHandler
    : ClientCommandIpcHandler<ListPaymentsIpcRequest, ListPaymentsClientRequest, ListPaymentsClientResponse,
        ListPaymentsIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ListPayments;

    public ListPaymentsIpcHandler(ILogger<ListPaymentsIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ListPaymentsClientRequest ToClientRequest(ListPaymentsIpcRequest request) =>
        request.ToClientRequest();

    protected override ListPaymentsIpcResponse ToIpcResponse(ListPaymentsClientResponse response) =>
        ListPaymentsIpcResponse.FromClientResponse(response);
}