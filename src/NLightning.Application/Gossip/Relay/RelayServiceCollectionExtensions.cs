using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Relay;

using Domain.Gossip.Interfaces;
using Graph;
using Interfaces;

public static class RelayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gossip relay (BOLT 7 plan G1-T7, G3-T3): <see cref="IGossipRelayScheduler"/>, the
    /// <see cref="GossipOriginTracker"/> (sized by <see cref="GossipRelayOptions.MaxTrackedOrigins"/>), the
    /// <see cref="IGossipPeerSender"/> (the peer's outbox through <see cref="IPeerGossipOutbox"/> when one is
    /// registered, NL-351) and, unless one is registered already, the <see cref="IGossipPeerDirectory"/> over the peer
    /// manager. The scheduler relays other nodes' gossip when the graph (<c>AddGossipGraphServices</c>) and the sync
    /// manager (<c>AddGossipSyncServices</c>) are registered too. Bind <see cref="GossipRelayOptions"/> from the
    /// <c>Gossip</c> section. Idempotent.
    /// </summary>
    public static IServiceCollection AddGossipRelayServices(this IServiceCollection services)
    {
        services.AddOptions<GossipRelayOptions>();
        services.TryAddSingleton<IGossipPeerDirectory, PeerManagerGossipPeerDirectory>();
        services.TryAddSingleton<IGossipPeerSender>(sp => new PeerGossipSender(sp));
        services.TryAddSingleton(sp => new GossipOriginTracker(
                                     sp.GetRequiredService<IOptions<GossipRelayOptions>>().Value.MaxTrackedOrigins));
        services.TryAddSingleton<IGossipRelayScheduler, GossipRelayScheduler>();
        return services;
    }

    /// <summary>
    /// Makes the peer services' <see cref="IGossipIngress"/> record the origin of every message they hand to the graph
    /// (<see cref="OriginTrackingGossipIngress"/> over the registered <see cref="GossipIngress"/>), so the relay never
    /// sends a message back to a peer that sent it (G3-T3). Call it after <c>AddGossipGraphServices</c>; it registers
    /// the relay services too. Idempotent.
    /// </summary>
    public static IServiceCollection AddGossipRelayOriginTracking(this IServiceCollection services)
    {
        services.AddGossipRelayServices();
        if (services.Any(d => d.ServiceType == typeof(IGossipIngress) && d.ImplementationFactory is not null
                           && d.ImplementationFactory.Method.DeclaringType == typeof(RelayServiceCollectionExtensions)))
            return services;

        services.Replace(ServiceDescriptor.Singleton<IGossipIngress>(CreateTrackingIngress));
        return services;
    }

    private static IGossipIngress CreateTrackingIngress(IServiceProvider sp) =>
        new OriginTrackingGossipIngress(sp.GetRequiredService<GossipIngress>(),
                                        sp.GetRequiredService<GossipOriginTracker>());
}