using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Node.Options;
using Options;

/// <summary>
/// Registers the node's fee estimate as one singleton <see cref="FeeService"/>.
/// </summary>
/// <remarks>
/// A typed HttpClient (<c>AddHttpClient&lt;IFeeService, FeeService&gt;</c>) is transient: every consumer got its own
/// instance, only the one the host started refreshed, and every other one (DustService, the close coordinator) read a
/// cache that never filled. Here the host and every consumer share one instance, which owns one long-lived
/// <see cref="HttpClient"/> (connections recycled every few minutes, so DNS changes are picked up).
/// </remarks>
public static class FeeServiceCollectionExtensions
{
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_connectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Registers <see cref="FeeService"/> and <see cref="IFeeService"/> as the same singleton. It reads
    /// <see cref="FeeEstimationOptions"/>, and for <see cref="FeeEstimationOptions.SourceBitcoind"/> also
    /// <see cref="BitcoinOptions"/> and <see cref="NodeOptions"/>, from DI.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="primaryHandler">The HTTP handler to use instead of the default one (tests); the fee service owns
    /// it.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddFeeServices(this IServiceCollection services,
                                                    Func<IServiceProvider, HttpMessageHandler>? primaryHandler = null)
    {
        services.RemoveAll<FeeService>();
        services.RemoveAll<IFeeService>();

        services.AddSingleton(sp =>
        {
            var handler = primaryHandler?.Invoke(sp)
                       ?? new SocketsHttpHandler { PooledConnectionLifetime = s_connectionLifetime };
            var httpClient = new HttpClient(handler) { Timeout = s_requestTimeout };
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            return new FeeService(sp.GetRequiredService<IOptions<FeeEstimationOptions>>(), httpClient,
                                  sp.GetRequiredService<ILogger<FeeService>>(),
                                  sp.GetService<IOptions<BitcoinOptions>>(), sp.GetService<IOptions<NodeOptions>>());
        });
        services.AddSingleton<IFeeService>(sp => sp.GetRequiredService<FeeService>());

        return services;
    }
}