using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Reports;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Interfaces;
using Export;

/// <summary>
/// The books' reports and exports (NL-602 A2, plan §6.1, IPC 43/44).
/// </summary>
public static class AccountingReportServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AccountingReportService"/> (<see cref="IAccountingReports"/>) and
    /// <see cref="AccountingExportService"/> (<see cref="IAccountingExports"/>) as singletons (TryAdd, idempotent).
    /// They read the books through scopes of their own and take <see cref="IAccountingBooks"/> when it is registered;
    /// without it (or with the books off) every report answers <see cref="AccountingBooksDisabledException"/>.
    /// </summary>
    public static IServiceCollection AddAccountingReportServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IAccountingReports>(sp => new AccountingReportService(
                                                         sp.GetRequiredService<IServiceScopeFactory>(),
                                                         sp.GetService<IAccountingBooks>(),
                                                         sp.GetRequiredService<ILogger<AccountingReportService>>(),
                                                         sp.GetService<IOptions<AccountingOptions>>(),
                                                         sp.GetService<IAccountingEventSealer>(),
                                                         sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IAccountingExports>(sp => new AccountingExportService(
                                                         sp.GetRequiredService<IServiceScopeFactory>(),
                                                         sp.GetService<IAccountingBooks>(),
                                                         sp.GetRequiredService<ILogger<AccountingExportService>>(),
                                                         sp.GetService<IOptions<AccountingOptions>>(),
                                                         sp.GetService<IAccountingEventSealer>()));

        return services;
    }
}