using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Gossip.Metrics;

public static class GossipMetricsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="GossipMetrics"/> as a singleton (idempotent). The graph and relay registrations call it,
    /// so the ingress, the relay and the sync record into the same meter.
    /// </summary>
    public static IServiceCollection AddGossipMetrics(this IServiceCollection services)
    {
        services.TryAddSingleton<GossipMetrics>();
        return services;
    }
}