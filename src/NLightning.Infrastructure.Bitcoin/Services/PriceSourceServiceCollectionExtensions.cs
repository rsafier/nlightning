using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Services;

using Accounting.Prices;
using Domain.Accounting.Prices;
using Domain.Node.Options;
using Infrastructure.Transport.Http;
using Infrastructure.Transport.Tor;

/// <summary>
/// Registers the price sources of the financial books (NL-602 A3-T2, D-A11), next to the fee service: the HTTP client
/// is built through <see cref="TorHttpHandler"/>, through Tor whenever Tor is on (<c>Hybrid</c> included, NL-677) unless
/// <c>Accounting:Prices:ThroughTor</c> is false (NL-868; <c>TorOnly</c> always through Tor), with a 64 KiB answer cap
/// (NL-678), and only when the HTTP source is configured.
/// </summary>
public static class PriceSourceServiceCollectionExtensions
{
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_connectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Registers <see cref="CsvPriceSource"/>, <see cref="HttpPriceSource"/> and <see cref="IPriceSource"/> (a
    /// <see cref="CompositePriceSource"/> of what <c>Accounting:Prices:Source</c> names: the file first; none for
    /// <c>None</c>), singletons. Reads <see cref="AccountingPriceOptions"/> and <c>Node:Tor</c>
    /// (<see cref="NodeOptions"/>) from DI (defaults when unbound).
    /// </summary>
    /// <exception cref="InvalidOperationException">At the HTTP source's resolution: <c>ThroughTor</c> true with Tor
    /// <c>Off</c>, or false in <c>TorOnly</c> (<see cref="AccountingPriceOptions.GetTorRoutingErrors"/>; the daemon
    /// refuses them at start), never a silent change of route.</exception>
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
            // NL-677: through Tor whenever Node:Tor:Mode is not Off (the asked hours mark when the node moved money),
            // unless the operator chose ThroughTor false (Hybrid only, NL-868)
            var options = GetOptions(sp);
            var tor = sp.GetService<IOptions<NodeOptions>>()?.Value.Tor;
            var errors = options.Value.GetTorRoutingErrors(tor);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join("; ", errors));

            var throughTor = options.Value.RoutesThroughTor(tor);
            var handler = primaryHandler?.Invoke(sp)
                       ?? TorHttpHandler.Create(sp, s_connectionLifetime, throughTorWhenEnabled: throughTor);
            var httpClient = new HttpClient(handler)
            {
                Timeout = s_requestTimeout,
                MaxResponseContentBufferSize = HttpResponseLimits.SmallResponseMaxBytes
            };
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            return new HttpPriceSource(httpClient, options, sp.GetRequiredService<ILogger<HttpPriceSource>>(),
                                       sp.GetService<TimeProvider>(), throughTor ? tor!.Mode : null);
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