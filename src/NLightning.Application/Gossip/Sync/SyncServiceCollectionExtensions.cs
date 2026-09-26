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
    /// <c>AddGossipGraphServices</c>), re-queries what <see cref="GossipIngress"/> dropped (NL-353), sizes and paces
    /// its <c>query_short_channel_ids</c> by the ingress queue (<see cref="GossipGraphOptions.MaxQueuedPerPeer"/>) and
    /// takes the tip
    /// from <see cref="IBlockchainMonitor"/> when one is registered. Bind <see cref="GossipSyncOptions"/> from the
    /// <c>Gossip</c> section. Idempotent. Nothing needs starting: the timers start with the first peer and stop when the
    /// container disposes the manager. Also binds <see cref="IGossipScidRefresher"/> to
    /// <see cref="GossipSyncScidRefresher"/> (G3-T5), replacing the payment layer's no-op default in either order.
    /// </summary>
    public static IServiceCollection AddGossipSyncServices(this IServiceCollection services)
    {
        services.AddOptions<GossipSyncOptions>();
        services.TryAddSingleton(sp =>
        {
            var ingress = sp.GetService<GossipIngress>();
            var monitor = sp.GetService<IBlockchainMonitor>();
            var graphOptions = sp.GetService<IOptions<GossipGraphOptions>>()?.Value ?? new GossipGraphOptions();
            return new GossipSyncManager(sp.GetRequiredService<IGraphStore>(),
                                         sp.GetRequiredService<IOptions<GossipSyncOptions>>(),
                                         sp.GetRequiredService<IOptions<NodeOptions>>(),
                                         sp.GetRequiredService<ILogger<GossipSyncManager>>(),
                                         sp.GetService<TimeProvider>(),
                                         (IGossipIngress?)ingress ?? sp.GetService<IGossipIngress>(),
                                         ingress is null ? null : ingress.TakeMissedShortChannelIds,
                                         monitor is null ? null : () => monitor.LastProcessedBlockHeight,
                                         ingress is null ? null : () => ingress.QueuedCount,
                                         ingress is null
                                             ? 0
                                             : Math.Min(graphOptions.MaxQueuedPerPeer, graphOptions.MaxQueued));
        });
        services.TryAddSingleton<IGossipSyncManager>(sp => sp.GetRequiredService<GossipSyncManager>());
        services.TryAddSingleton<IGossipSyncService>(sp => sp.GetRequiredService<GossipSyncManager>());
        // G3-T5: the payment retry path's refresh goes to the sync (replaces AddPaymentSendServices' no-op default)
        services.Replace(ServiceDescriptor.Singleton<IGossipScidRefresher, GossipSyncScidRefresher>());
        return services;
    }
}