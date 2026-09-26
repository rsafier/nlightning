using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Gossip.Graph;

using Domain.Gossip.Interfaces;
using Interfaces;

public static class GossipGraphServiceCollectionExtensions
{
    /// <summary>
    /// Registers the graph (plan BOLT7 G2-T4) as singletons (TryAdd, so idempotent): <see cref="IGraphStore"/>
    /// (<see cref="GraphStore"/>, persisted through the scoped <c>IUnitOfWork</c>), and <see cref="GossipIngress"/> as
    /// itself, <see cref="IGossipIngress"/> (the peer service's entry point) and <see cref="IOwnGossipSink"/> (our own
    /// announcements). It needs <c>IGossipSignatureVerifier</c> and <c>IFundingOutputLookup</c>
    /// (<c>AddBitcoinInfrastructure</c>) and <c>IOptions&lt;NodeOptions&gt;</c>; bind
    /// <see cref="GossipGraphOptions"/> from the <c>Gossip</c> section. The host should start the ingress
    /// (<see cref="GossipIngress.StartAsync"/>, otherwise it starts with the first message) and stop it on shutdown
    /// (<see cref="GossipIngress.StopAsync"/>, which writes the pending graph changes). <see cref="GraphPruner"/>
    /// (G2-T5, needs <c>IBlockchainMonitor</c>) is registered too; the host calls <see cref="GraphPruner.Start"/> before
    /// the chain monitor starts and <see cref="GraphPruner.StopAsync"/> on shutdown.
    /// </summary>
    public static IServiceCollection AddGossipGraphServices(this IServiceCollection services)
    {
        services.AddOptions<GossipGraphOptions>();
        services.TryAddSingleton<IGraphStore, GraphStore>();
        services.TryAddSingleton<GossipIngress>();
        services.TryAddSingleton<IGossipIngress>(sp => sp.GetRequiredService<GossipIngress>());
        services.TryAddSingleton<IOwnGossipSink>(sp => sp.GetRequiredService<GossipIngress>());
        services.TryAddSingleton<GraphPruner>();
        return services;
    }
}