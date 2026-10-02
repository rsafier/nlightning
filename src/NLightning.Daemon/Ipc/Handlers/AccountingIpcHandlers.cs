using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Ipc.Handlers;

using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// Serves <c>listaccountingevents</c> (ClientCommand 41, NL-602): a page of sealed accounting events after a cursor.
/// </summary>
internal sealed class ListAccountingEventsIpcHandler
    : ClientCommandIpcHandler<ListAccountingEventsIpcRequest, ListAccountingEventsClientRequest,
        ListAccountingEventsClientResponse, ListAccountingEventsIpcResponse>
{
    public override ClientCommand Command => ClientCommand.ListAccountingEvents;

    public ListAccountingEventsIpcHandler(ILogger<ListAccountingEventsIpcHandler> logger,
                                          IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override ListAccountingEventsClientRequest ToClientRequest(ListAccountingEventsIpcRequest request) =>
        request.ToClientRequest();

    protected override ListAccountingEventsIpcResponse ToIpcResponse(ListAccountingEventsClientResponse response) =>
        ListAccountingEventsIpcResponse.FromClientResponse(response);
}

/// <summary>
/// Serves <c>accountingsnapshot</c> (ClientCommand 42, NL-602): the node's live balances by bucket.
/// </summary>
internal sealed class AccountingSnapshotIpcHandler
    : ClientCommandIpcHandler<AccountingSnapshotIpcRequest, AccountingSnapshotClientRequest,
        AccountingSnapshotClientResponse, AccountingSnapshotIpcResponse>
{
    public override ClientCommand Command => ClientCommand.AccountingSnapshot;

    public AccountingSnapshotIpcHandler(ILogger<AccountingSnapshotIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override AccountingSnapshotClientRequest ToClientRequest(AccountingSnapshotIpcRequest request) =>
        request.ToClientRequest();

    protected override AccountingSnapshotIpcResponse ToIpcResponse(AccountingSnapshotClientResponse response) =>
        AccountingSnapshotIpcResponse.FromClientResponse(response);
}