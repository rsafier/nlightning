using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Persistence.Interfaces;
using Handlers;
using Interfaces;

/// <summary>
/// The accounting commands <c>listaccountingevents</c> (ClientCommand 41) and <c>accountingsnapshot</c> (42), NL-602.
/// </summary>
public static class AccountingIpcServiceExtensions
{
    /// <summary>
    /// Registers both client handlers (scoped; a node without the Application's accounting services lists without
    /// sealing first and answers "not available" for the snapshot) and their IPC handlers. Idempotent (every
    /// registration is a TryAdd).
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
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, ListAccountingEventsIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, AccountingSnapshotIpcHandler>());

        return services;
    }
}