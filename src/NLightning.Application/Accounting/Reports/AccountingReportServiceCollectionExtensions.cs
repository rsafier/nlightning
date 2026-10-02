using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Reports;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Export;
using Domain.Accounting.Financial.Reports;
using Domain.Accounting.Interfaces;
using Domain.Bitcoin.Interfaces;
using Export;
using Export.Financial;
using Financial;

/// <summary>
/// The books' reports and exports (NL-602 A2, plan §6.1, IPC 43/44).
/// </summary>
public static class AccountingReportServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AccountingReportService"/> (<see cref="IAccountingReports"/>) and
    /// <see cref="AccountingExportService"/> (<see cref="IAccountingExports"/>) as singletons (TryAdd, idempotent).
    /// They read the books through scopes of their own and take <see cref="IAccountingBooks"/> when it is registered;
    /// without it (or with the books off) every report answers <see cref="AccountingBooksDisabledException"/>. The
    /// optional <see cref="IBlockTimeSource"/> dates a channel opened before the feed began by its funding block (NL-623).
    /// </summary>
    public static IServiceCollection AddAccountingReportServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IAccountingReports>(sp => new AccountingReportService(
                                                         sp.GetRequiredService<IServiceScopeFactory>(),
                                                         sp.GetService<IAccountingBooks>(),
                                                         sp.GetRequiredService<ILogger<AccountingReportService>>(),
                                                         sp.GetService<IOptions<AccountingOptions>>(),
                                                         sp.GetService<IAccountingEventSealer>(),
                                                         sp.GetService<TimeProvider>(),
                                                         sp.GetService<IBlockTimeSource>()));
        services.TryAddSingleton<IAccountingExports>(sp => new AccountingExportService(
                                                         sp.GetRequiredService<IServiceScopeFactory>(),
                                                         sp.GetService<IAccountingBooks>(),
                                                         sp.GetRequiredService<ILogger<AccountingExportService>>(),
                                                         sp.GetService<IOptions<AccountingOptions>>(),
                                                         sp.GetService<IAccountingEventSealer>()));

        // The financial book's reports and exports (NL-602 A3-T6): IPC 43/44 with --book financial
        services.TryAddSingleton<IAccountingFinancialReports>(sp => new AccountingFinancialReportService(
                                                                   sp.GetRequiredService<IServiceScopeFactory>(),
                                                                   sp.GetService<IAccountingBooks>(),
                                                                   sp.GetRequiredService<
                                                                       ILogger<AccountingFinancialReportService>>(),
                                                                   sp.GetService<IOptions<AccountingOptions>>(),
                                                                   sp.GetService<IAccountingEventSealer>(),
                                                                   sp.GetService<IFinancialBooksProjector>(),
                                                                   sp.GetService<INodeSnapshotSource>(),
                                                                   sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IAccountingFinancialExports>(sp => new AccountingFinancialExportService(
                                                                   sp.GetRequiredService<IServiceScopeFactory>(),
                                                                   sp.GetService<IAccountingBooks>(),
                                                                   sp.GetRequiredService<
                                                                       ILogger<AccountingFinancialExportService>>(),
                                                                   sp.GetService<IAccountingEventSealer>(),
                                                                   sp.GetService<IFinancialBooksProjector>()));

        return services;
    }
}