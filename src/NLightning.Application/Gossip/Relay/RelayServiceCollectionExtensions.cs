using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Gossip.Relay;

using Interfaces;

public static class RelayServiceCollectionExtensions
{
    /// <summary>
    /// Registers the relay of our own gossip (BOLT 7 plan G1-T7): <see cref="IGossipRelayScheduler"/> and, unless one is
    /// registered already, the <see cref="IGossipPeerDirectory"/> over the peer manager. Idempotent.
    /// </summary>
    public static IServiceCollection AddGossipRelayServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IGossipPeerDirectory, PeerManagerGossipPeerDirectory>();
        services.TryAddSingleton<IGossipRelayScheduler, GossipRelayScheduler>();
        return services;
    }
}