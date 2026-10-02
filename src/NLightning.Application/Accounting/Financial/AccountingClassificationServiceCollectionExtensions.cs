using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Interfaces;

/// <summary>The financial book's classification services (NL-602 A3-T3).</summary>
public static class AccountingClassificationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AccountingClassificationService"/> (singleton, as itself and as
    /// <see cref="IAccountingClassificationAdmin"/>, one instance). Idempotent (TryAdd); called by
    /// <see cref="AccountingServiceCollectionExtensions.AddAccountingServices"/>.
    /// </summary>
    public static IServiceCollection AddAccountingClassificationServices(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new AccountingClassificationService(
                                     sp.GetRequiredService<IServiceScopeFactory>(),
                                     sp.GetRequiredService<ILogger<AccountingClassificationService>>(),
                                     sp.GetService<IOptions<AccountingOptions>>(), sp.GetService<IAccountingBooks>(),
                                     sp.GetService<IAccountingEventSealer>(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IAccountingClassificationAdmin>(
            sp => sp.GetRequiredService<AccountingClassificationService>());
        return services;
    }
}