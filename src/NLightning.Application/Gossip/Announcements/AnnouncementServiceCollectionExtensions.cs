using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Gossip.Announcements;

using Domain.Gossip.Interfaces;
using Interfaces;

public static class AnnouncementServiceCollectionExtensions
{
    /// <summary>
    /// Registers the announcement of our public channels (BOLT 7 plan G1): <see cref="IChannelAnnouncementService"/>
    /// and, unless one is registered already, the no-op <see cref="IOwnGossipSink"/> (the graph store replaces it).
    /// Idempotent.
    /// </summary>
    public static IServiceCollection AddGossipAnnouncementServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IOwnGossipSink, NullOwnGossipSink>();
        services.TryAddSingleton<IChannelAnnouncementService, ChannelAnnouncementService>();
        return services;
    }
}