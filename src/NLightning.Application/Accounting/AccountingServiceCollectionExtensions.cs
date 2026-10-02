using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting;

using Backfill;
using Books;
using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Prices;
using Domain.Accounting.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Financial;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Prices;
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
    /// defaults apply. Also the backfill (<see cref="AccountingBackfillService"/> as itself and as
    /// <see cref="IAccountingBackfill"/>: the host awaits its cutover before the peers start and starts its memo pass
    /// after the chain monitor). And the period close (A3-T5): <see cref="AccountingPeriodService"/> as itself, as
    /// <see cref="IAccountingPeriods"/> and as the lock's <see cref="IAccountingAdjustmentSink"/> (one instance), with
    /// the off <see cref="NullFinancialBooksProjector"/> as <see cref="IFinancialBooksProjector"/> until A3-T4 registers
    /// its own.
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
        services.TryAddSingleton<AccountingFeedGate>();
        services.TryAddSingleton(sp => new AccountingBackfillService(
                                     sp.GetRequiredService<IServiceScopeFactory>(),
                                     sp.GetRequiredService<ILogger<AccountingBackfillService>>(),
                                     sp.GetService<TimeProvider>(),
                                     feedGate: sp.GetService<AccountingFeedGate>()));
        services.TryAddSingleton<IAccountingBackfill>(sp => sp.GetRequiredService<AccountingBackfillService>());
        services.TryAddSingleton(sp => new AccountingBooksService(
                                     sp.GetRequiredService<IServiceScopeFactory>(),
                                     sp.GetRequiredService<ILogger<AccountingBooksService>>(),
                                     sp.GetService<IOptions<AccountingOptions>>(),
                                     sp.GetService<IAccountingEventSealer>(), sp.GetService<INodeSnapshotSource>(),
                                     sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IAccountingBooks>(sp => sp.GetRequiredService<AccountingBooksService>());
        services.AddAccountingReportServices();
        services.AddAccountingClassificationServices();
        services.AddAccountingPriceValuation();
        services.AddAccountingPeriodServices();

        return services;
    }

    /// <summary>
    /// The back-valuation of the financial books (NL-602 A3-T2): <see cref="PriceValuationService"/> as itself and as
    /// <see cref="IAccountingPrices"/> (one instance; the host starts it after the books and stops it before), and the
    /// period lock's adjustment rule <see cref="IAccountingAdjustmentSink"/> (default
    /// <see cref="NullAccountingAdjustmentSink"/>; A3-T5 registers its own before this call, or replaces it). The price
    /// sources (<see cref="IPriceSource"/>) come from the host (<c>AddAccountingPriceSources</c>, Infrastructure.Bitcoin);
    /// without one the job values with stored prices only. Idempotent (TryAdd).
    /// </summary>
    public static IServiceCollection AddAccountingPriceValuation(this IServiceCollection services)
    {
        services.TryAddSingleton<IAccountingAdjustmentSink>(NullAccountingAdjustmentSink.Instance);
        services.TryAddSingleton(sp => new PriceValuationService(
                                     sp.GetRequiredService<IServiceScopeFactory>(),
                                     sp.GetRequiredService<ILogger<PriceValuationService>>(),
                                     sp.GetService<IOptions<AccountingOptions>>(),
                                     sp.GetService<IOptions<AccountingPriceOptions>>(),
                                     sp.GetService<IPriceSource>(), sp.GetService<IAccountingAdjustmentSink>(),
                                     sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IAccountingPrices>(sp => sp.GetRequiredService<PriceValuationService>());

        return services;
    }

    /// <summary>
    /// Period close, lock and signed digests (NL-602 A3-T5): <see cref="AccountingPeriodService"/> as itself, as
    /// <see cref="IAccountingPeriods"/> and as the period lock's <see cref="IAccountingAdjustmentSink"/> (replacing the
    /// off default of <see cref="AddAccountingPriceValuation"/> whatever the order). The financial projector is A3-T4's:
    /// until it registers its own (before this call, or with Replace), the default is off and closes are refused.
    /// </summary>
    public static IServiceCollection AddAccountingPeriodServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IFinancialBooksProjector, NullFinancialBooksProjector>();
        services.TryAddSingleton(sp => new AccountingPeriodService(
                                     sp.GetRequiredService<IServiceScopeFactory>(),
                                     sp.GetRequiredService<ILogger<AccountingPeriodService>>(),
                                     sp.GetService<IAccountingBooks>(), sp.GetService<IFinancialBooksProjector>(),
                                     sp.GetService<ILightningSigner>(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IAccountingPeriods>(sp => sp.GetRequiredService<AccountingPeriodService>());
        // Replace, not TryAdd: the lock must win over the off NullAccountingAdjustmentSink default whatever the order
        services.Replace(ServiceDescriptor.Singleton<IAccountingAdjustmentSink>(
                             sp => sp.GetRequiredService<AccountingPeriodService>()));

        return services;
    }
}