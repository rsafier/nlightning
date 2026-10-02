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

/// <summary>
/// Serves <c>accounting report</c> (ClientCommand 43, NL-602 A2): one report of the operational books.
/// </summary>
internal sealed class AccountingReportIpcHandler
    : ClientCommandIpcHandler<AccountingReportIpcRequest, AccountingReportClientRequest,
        AccountingReportClientResponse, AccountingReportIpcResponse>
{
    public override ClientCommand Command => ClientCommand.AccountingReport;

    public AccountingReportIpcHandler(ILogger<AccountingReportIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override AccountingReportClientRequest ToClientRequest(AccountingReportIpcRequest request) =>
        request.ToClientRequest();

    protected override AccountingReportIpcResponse ToIpcResponse(AccountingReportClientResponse response) =>
        AccountingReportIpcResponse.FromClientResponse(response);
}

/// <summary>
/// Serves <c>accounting export</c> (ClientCommand 44, NL-602 A2): one page of an export, streamed back.
/// </summary>
internal sealed class AccountingExportIpcHandler
    : ClientCommandIpcHandler<AccountingExportIpcRequest, AccountingExportClientRequest,
        AccountingExportClientResponse, AccountingExportIpcResponse>
{
    public override ClientCommand Command => ClientCommand.AccountingExport;

    public AccountingExportIpcHandler(ILogger<AccountingExportIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override AccountingExportClientRequest ToClientRequest(AccountingExportIpcRequest request) =>
        request.ToClientRequest();

    protected override AccountingExportIpcResponse ToIpcResponse(AccountingExportClientResponse response) =>
        AccountingExportIpcResponse.FromClientResponse(response);
}

/// <summary>
/// Serves <c>accounting reconcile|rebuild|verify</c> (ClientCommand 45, NL-602 A2).
/// </summary>
internal sealed class AccountingAdminIpcHandler
    : ClientCommandIpcHandler<AccountingAdminIpcRequest, AccountingAdminClientRequest,
        AccountingAdminClientResponse, AccountingAdminIpcResponse>
{
    public override ClientCommand Command => ClientCommand.AccountingAdmin;

    public AccountingAdminIpcHandler(ILogger<AccountingAdminIpcHandler> logger, IServiceProvider serviceProvider)
        : base(logger, serviceProvider)
    {
    }

    protected override AccountingAdminClientRequest ToClientRequest(AccountingAdminIpcRequest request) =>
        request.ToClientRequest();

    protected override AccountingAdminIpcResponse ToIpcResponse(AccountingAdminClientResponse response) =>
        AccountingAdminIpcResponse.FromClientResponse(response);
}