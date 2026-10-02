using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Extensions;

using Application.Accounting;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial.Export;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Persistence.Interfaces;
using Handlers;
using Interfaces;

/// <summary>
/// The accounting commands <c>listaccountingevents</c> (ClientCommand 41), <c>accountingsnapshot</c> (42), NL-602, and
/// the books' <c>accounting report</c> (43), <c>accounting export</c> (44) and <c>accounting
/// reconcile|rebuild|verify</c> (45), NL-602 A2.
/// </summary>
public static class AccountingIpcServiceExtensions
{
    /// <summary>
    /// Registers the client handlers (scoped; a node without the Application's accounting services lists without
    /// sealing first, answers "not available" for the snapshot and "books disabled" for the books' commands, except
    /// verify) and their IPC handlers. Idempotent (every registration is a TryAdd).
    /// </summary>
    public static IServiceCollection AddAccountingIpcServices(this IServiceCollection services)
    {
        services.TryAddScoped<IClientCommandHandler<ListAccountingEventsClientRequest,
            ListAccountingEventsClientResponse>>(sp => new ListAccountingEventsClientHandler(
                                                     sp.GetRequiredService<IUnitOfWork>(),
                                                     sp.GetRequiredService<ILogger<ListAccountingEventsClientHandler>>(),
                                                     sp.GetService<IAccountingEventSealer>(),
                                                     sp.GetService<IChannelMemoryRepository>()));
        services.TryAddScoped<IClientCommandHandler<AccountingSnapshotClientRequest,
            AccountingSnapshotClientResponse>>(sp => new AccountingSnapshotClientHandler(
                                                   sp.GetService<INodeSnapshotSource>()));
        services.TryAddScoped<IClientCommandHandler<AccountingReportClientRequest,
            AccountingReportClientResponse>>(sp => new AccountingReportClientHandler(
                                                 sp.GetService<IAccountingReports>(),
                                                 sp.GetService<IOptions<AccountingOptions>>(),
                                                 sp.GetService<IChannelMemoryRepository>(),
                                                 sp.GetService<IAccountingFinancialReports>()));
        services.TryAddScoped<IClientCommandHandler<AccountingExportClientRequest,
            AccountingExportClientResponse>>(sp => new AccountingExportClientHandler(
                                                 sp.GetService<IAccountingExports>(),
                                                 sp.GetService<IAccountingFinancialExports>()));
        services.TryAddScoped<IClientCommandHandler<AccountingAdminClientRequest,
            AccountingAdminClientResponse>>(sp => new AccountingAdminClientHandler(
                                                sp.GetRequiredService<IUnitOfWork>(),
                                                sp.GetService<IAccountingBooks>(),
                                                sp.GetService<IOptions<AccountingOptions>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, ListAccountingEventsIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, AccountingSnapshotIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, AccountingReportIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, AccountingExportIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, AccountingAdminIpcHandler>());

        return services;
    }
}