using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Sync;

using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Gossip.Interfaces;
using Domain.Node.Options;
using Graph;
using Graph.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;
using Metrics;
using Relay.Interfaces;

public static class SyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gossip query and sync side (BOLT 7 plan G3-T1/G3-T2) as one singleton: <see cref="GossipSyncManager"/>
    /// as itself, <see cref="IGossipSyncManager"/> and <see cref="IGossipSyncService"/> (the peer service's port for
    /// messages 261-265 and its init hook). It reads the graph (<see cref="IGraphStore"/>, from
    /// <c>AddGossipGraphServices</c>), re-queries what <see cref="GossipIngress"/> dropped (NL-353), sizes and paces
    /// its <c>query_short_channel_ids</c> by the ingress queue (<see cref="GossipGraphOptions.MaxQueuedPerPeer"/>) and
    /// takes the tip
    /// from <see cref="IBlockchainMonitor"/> when one is registered. Its queries, replies and filters go out through
    /// the relay's <see cref="IGossipPeerSender"/> when one is registered (the peer's outbox, NL-361). Bind
    /// <see cref="GossipSyncOptions"/> from the <c>Gossip</c> section. Idempotent. Nothing needs starting: the timers
    /// start with the first peer and stop when the container disposes the manager. Also binds
    /// <see cref="IGossipScidRefresher"/> to <see cref="GossipSyncScidRefresher"/> (G3-T5), replacing the payment
    /// layer's no-op default in either order.
    /// </summary>
    public static IServiceCollection AddGossipSyncServices(this IServiceCollection services)
    {
        services.AddOptions<GossipSyncOptions>();
        services.TryAddSingleton(sp =>
        {
            var ingress = sp.GetService<GossipIngress>();
            var monitor = sp.GetService<IBlockchainMonitor>();
            var graphOptions = sp.GetService<IOptions<GossipGraphOptions>>()?.Value ?? new GossipGraphOptions();
            var channels = sp.GetService<IChannelMemoryRepository>();

            // NL-363: sync peers are channel peers first
            Func<CompactPubKey, bool>? hasChannelWith = channels is null
                ? null
                : peer => channels.FindChannels(c => c.RemoteNodeId == peer
                                                  && c.State is not (ChannelState.Closed or ChannelState.Failed))
                                   .Count > 0;
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
                                             : Math.Min(graphOptions.MaxQueuedPerPeer, graphOptions.MaxQueued),
                                         sp.GetService<GossipMetrics>(),
                                         ingress is null ? null : ingress.QueuedCountOf,
                                         GetPendingChannels(sp, ingress),
                                         hasChannelWith,
                                         sp.GetService<IGossipPeerSender>());
        });
        services.TryAddSingleton<IGossipSyncManager>(sp => sp.GetRequiredService<GossipSyncManager>());
        services.TryAddSingleton<IGossipSyncService>(sp => sp.GetRequiredService<GossipSyncManager>());
        // G3-T5: the payment retry path's refresh goes to the sync (replaces AddPaymentSendServices' no-op default)
        services.Replace(ServiceDescriptor.Singleton<IGossipScidRefresher, GossipSyncScidRefresher>());
        return services;
    }

    /// <summary>
    /// The pending-channel views the sync asks (NL-415, NL-414): every registered <see cref="IGossipPendingChannels"/>,
    /// plus the graph ingress and the funding output lookup when they implement it (so a new implementation needs no
    /// registration line). Duplicates are removed by the manager.
    /// </summary>
    private static List<IGossipPendingChannels> GetPendingChannels(IServiceProvider sp, GossipIngress? ingress)
    {
        var sources = sp.GetServices<IGossipPendingChannels>().ToList();
        if (((object?)ingress ?? sp.GetService<IGossipIngress>()) is IGossipPendingChannels ingressView)
            sources.Add(ingressView);
        if (sp.GetService<IFundingOutputLookup>() is IGossipPendingChannels lookupView)
            sources.Add(lookupView);
        return sources;
    }
}