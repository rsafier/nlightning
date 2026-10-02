using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting;

using Books;
using Domain.Accounting.Books;
using Domain.Accounting.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Reports;

/// <summary>
/// The accounting feed's services (NL-602, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §7).
/// </summary>
public static class AccountingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the sealer (<see cref="AccountingEventSealerService"/> as itself and as
    /// <see cref="IAccountingEventSealer"/>, one instance; the host starts it after the chain monitor and stops it
    /// before), the balance snapshot source (<see cref="INodeSnapshotSource"/>) and the operational books
    /// (<see cref="AccountingBooksService"/> as itself and as <see cref="IAccountingBooks"/>, one instance; the host
    /// starts it after the sealer and stops it before) with its reports and exports
    /// (<see cref="AccountingReportServiceCollectionExtensions.AddAccountingReportServices"/>). Idempotent (TryAdd). The host binds
    /// <see cref="AccountingOptions"/> from <see cref="AccountingOptions.SectionName"/>; without a binding the
    /// defaults apply.
    /// </summary>
    public static IServiceCollection AddAccountingServices(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new AccountingEventSealerService(
                                     sp.GetRequiredService<IServiceScopeFactory>(),
                                     sp.GetRequiredService<ILogger<AccountingEventSealerService>>(),
                                     sp.GetService<IOptions<AccountingOptions>>(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IAccountingEventSealer>(sp => sp.GetRequiredService<AccountingEventSealerService>());
        services.TryAddSingleton<INodeSnapshotSource>(sp => new NodeSnapshotSource(
                                                          sp.GetRequiredService<IChannelMemoryRepository>(),
                                                          sp.GetRequiredService<IUtxoMemoryRepository>(),
                                                          sp.GetRequiredService<IServiceScopeFactory>(),
                                                          sp.GetService<IBlockchainMonitor>(),
                                                          sp.GetService<TimeProvider>()));
        services.TryAddSingleton(sp => new AccountingBooksService(
                                     sp.GetRequiredService<IServiceScopeFactory>(),
                                     sp.GetRequiredService<ILogger<AccountingBooksService>>(),
                                     sp.GetService<IOptions<AccountingOptions>>(),
                                     sp.GetService<IAccountingEventSealer>(), sp.GetService<INodeSnapshotSource>(),
                                     sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IAccountingBooks>(sp => sp.GetRequiredService<AccountingBooksService>());
        services.AddAccountingReportServices();

        return services;
    }
}