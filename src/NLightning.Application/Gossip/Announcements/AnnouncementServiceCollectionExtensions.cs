using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Gossip.Announcements;

using Domain.Gossip.Interfaces;
using Interfaces;
using Relay;

public static class AnnouncementServiceCollectionExtensions
{
    /// <summary>
    /// Registers the announcement of our public channels and node (BOLT 7 plan G1): <see cref="IChannelAnnouncementService"/>,
    /// <see cref="INodeAnnouncementService"/>, the <see cref="OwnGossipPublisher"/> and the relay of our own gossip
    /// (<see cref="RelayServiceCollectionExtensions.AddGossipRelayServices"/>), and, unless one is registered already,
    /// the no-op <see cref="IOwnGossipSink"/> (the graph store replaces it). Idempotent.
    /// </summary>
    public static IServiceCollection AddGossipAnnouncementServices(this IServiceCollection services)
    {
        services.AddGossipRelayServices();
        services.TryAddSingleton<IOwnGossipSink, NullOwnGossipSink>();
        services.TryAddSingleton<OwnGossipPublisher>();
        services.TryAddSingleton<INodeAnnouncementService, NodeAnnouncementService>();
        services.TryAddSingleton<IChannelAnnouncementService, ChannelAnnouncementService>();
        return services;
    }
}