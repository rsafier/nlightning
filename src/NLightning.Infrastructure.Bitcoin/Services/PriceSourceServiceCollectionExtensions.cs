using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Services;

using Accounting.Prices;
using Domain.Accounting.Prices;
using Infrastructure.Transport.Tor;

/// <summary>
/// Registers the price sources of the financial books (NL-602 A3-T2, D-A11), next to the fee service: the HTTP client
/// is built the same way (<see cref="TorHttpHandler"/>: through Tor in <c>TorOnly</c>) and only when the HTTP source
/// is configured.
/// </summary>
public static class PriceSourceServiceCollectionExtensions
{
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_connectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Registers <see cref="CsvPriceSource"/>, <see cref="HttpPriceSource"/> and <see cref="IPriceSource"/> (a
    /// <see cref="CompositePriceSource"/> of what <c>Accounting:Prices:Source</c> names: the file first; none for
    /// <c>None</c>), singletons. Reads <see cref="AccountingPriceOptions"/> from DI (defaults when unbound).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="primaryHandler">The HTTP handler to use instead of the default one (tests); the source owns it.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddAccountingPriceSources(
        this IServiceCollection services, Func<IServiceProvider, HttpMessageHandler>? primaryHandler = null)
    {
        services.RemoveAll<CsvPriceSource>();
        services.RemoveAll<HttpPriceSource>();
        services.RemoveAll<IPriceSource>();

        services.AddSingleton(sp => new CsvPriceSource(GetOptions(sp),
                                                       sp.GetRequiredService<ILogger<CsvPriceSource>>(),
                                                       sp.GetService<TimeProvider>()));
        services.AddSingleton(sp =>
        {
            var handler = primaryHandler?.Invoke(sp) ?? TorHttpHandler.Create(sp, s_connectionLifetime);
            var httpClient = new HttpClient(handler) { Timeout = s_requestTimeout };
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            return new HttpPriceSource(httpClient, GetOptions(sp), sp.GetRequiredService<ILogger<HttpPriceSource>>(),
                                       sp.GetService<TimeProvider>());
        });
        services.AddSingleton<IPriceSource>(sp =>
        {
            var options = GetOptions(sp);
            var sources = new List<IPriceSource>();
            if (options.Value.UsesCsv)
                sources.Add(sp.GetRequiredService<CsvPriceSource>());
            if (options.Value.UsesHttp)
                sources.Add(sp.GetRequiredService<HttpPriceSource>());

            return new CompositePriceSource(sources, options);
        });

        return services;
    }

    private static IOptions<AccountingPriceOptions> GetOptions(IServiceProvider serviceProvider) =>
        serviceProvider.GetService<IOptions<AccountingPriceOptions>>()
     ?? Microsoft.Extensions.Options.Options.Create(new AccountingPriceOptions());
}