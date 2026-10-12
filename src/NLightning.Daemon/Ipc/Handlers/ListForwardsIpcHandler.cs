using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// Serves <c>listforwards</c> (ClientCommand 40, NL-597): a page of forwarded payments with the totals and the
/// refused-HTLC counters (NL-598).
/// </summary>
internal sealed class ListForwardsIpcHandler
    : ClientCommandIpcHandler<ListForwardsIpcRequest, ListForwardsClientRequest, ListForwardsClientResponse,
        ListForwardsIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ListForwards;

    public ListForwardsIpcHandler(ILogger<ListForwardsIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ListForwardsClientRequest ToClientRequest(ListForwardsIpcRequest request) =>
        request.ToClientRequest();

    protected override ListForwardsIpcResponse ToIpcResponse(ListForwardsClientResponse response) =>
        ListForwardsIpcResponse.FromClientResponse(response);
}