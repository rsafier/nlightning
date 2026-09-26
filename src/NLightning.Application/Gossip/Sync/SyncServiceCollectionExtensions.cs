using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Sync;

using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Graph;
using Graph.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

public static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gossip query and sync side (BOLT 7 plan G3-T1/G3-T2) as one singleton: <see cref="GossipSyncManager"/>
    /// as itself, <see cref="IGossipSyncManager"/> and <see cref="IGossipSyncService"/> (the peer service's port for
    /// messages 261-265 and its init hook). It reads the graph (<see cref="IGraphStore"/>, from
    /// <c>AddGossipGraphServices</c>), re-queries what <see cref="GossipIngress"/> dropped (NL-353) and takes the tip
    /// from <see cref="IBlockchainMonitor"/> when one is registered. Bind <see cref="GossipSyncOptions"/> from the
    /// <c>Gossip</c> section. Idempotent. Nothing needs starting: the timers start with the first peer and stop when the
    /// container disposes the manager.
    /// </summary>
    public static IServiceCollection AddGossipSyncServices(this IServiceCollection services)
    {
        services.AddOptions<GossipSyncOptions>();
        services.TryAddSingleton(sp =>
        {
            var ingress = sp.GetService<GossipIngress>();
            var monitor = sp.GetService<IBlockchainMonitor>();
            return new GossipSyncManager(sp.GetRequiredService<IGraphStore>(),
                                         sp.GetRequiredService<IOptions<GossipSyncOptions>>(),
                                         sp.GetRequiredService<IOptions<NodeOptions>>(),
                                         sp.GetRequiredService<ILogger<GossipSyncManager>>(),
                                         sp.GetService<TimeProvider>(),
                                         (IGossipIngress?)ingress ?? sp.GetService<IGossipIngress>(),
                                         ingress is null ? null : ingress.TakeMissedShortChannelIds,
                                         monitor is null ? null : () => monitor.LastProcessedBlockHeight);
        });
        services.TryAddSingleton<IGossipSyncManager>(sp => sp.GetRequiredService<GossipSyncManager>());
        services.TryAddSingleton<IGossipSyncService>(sp => sp.GetRequiredService<GossipSyncManager>());
        return services;
    }
}