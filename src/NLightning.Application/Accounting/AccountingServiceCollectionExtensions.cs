using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting;

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
    /// before) and the balance snapshot source (<see cref="INodeSnapshotSource"/>). Idempotent (TryAdd). The host binds
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
        services.AddAccountingReportServices();

        return services;
    }
}