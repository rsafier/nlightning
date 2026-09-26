using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Gossip.Graph;

using Domain.Gossip.Interfaces;
using Interfaces;
using Metrics;

public static class GossipGraphServiceCollectionExtensions
{
    /// <summary>
    /// Registers the graph (plan BOLT7 G2-T4) as singletons (idempotent): <see cref="IGraphStore"/>
    /// (<see cref="GraphStore"/>, persisted through the scoped <c>IUnitOfWork</c>), and <see cref="GossipIngress"/> as
    /// itself, <see cref="IGossipIngress"/> (the peer service's entry point) and <see cref="IOwnGossipSink"/> (our own
    /// announcements; it replaces any sink registered before, such as the no-op default of the G1 services). It needs <c>IGossipSignatureVerifier</c> and <c>IFundingOutputLookup</c>
    /// (<c>AddBitcoinInfrastructure</c>) and <c>IOptions&lt;NodeOptions&gt;</c>; bind
    /// <see cref="GossipGraphOptions"/> from the <c>Gossip</c> section. The host should start the ingress
    /// (<see cref="GossipIngress.StartAsync"/>, otherwise it starts with the first message) and stop it on shutdown
    /// (<see cref="GossipIngress.StopAsync"/>, which writes the pending graph changes). <see cref="GraphPruner"/>
    /// (G2-T5, needs <c>IBlockchainMonitor</c>) is registered too; the host calls <see cref="GraphPruner.Start"/> before
    /// the chain monitor starts and <see cref="GraphPruner.StopAsync"/> on shutdown. Also registers
    /// <see cref="GossipMetrics"/> (<c>AddGossipMetrics</c>), which the ingress records into.
    /// </summary>
    public static IServiceCollection AddGossipGraphServices(this IServiceCollection services)
    {
        services.AddOptions<GossipGraphOptions>();
        services.AddGossipMetrics();
        services.TryAddSingleton<IGraphStore, GraphStore>();
        services.TryAddSingleton<GossipIngress>();
        services.TryAddSingleton<IGossipIngress>(sp => sp.GetRequiredService<GossipIngress>());
        // Replaces the no-op default of the announcement services (G1), whichever registration ran first
        services.Replace(ServiceDescriptor.Singleton<IOwnGossipSink>(sp => sp.GetRequiredService<GossipIngress>()));
        services.TryAddSingleton<GraphPruner>();
        return services;
    }
}