using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Gossip;

using Interfaces;
using Services;

public static class GossipServiceCollectionExtensions
{
    /// <summary>
    /// Registers the direct <c>channel_update</c> exchange (<see cref="IChannelUpdateService"/>). <c>PeerManager</c>
    /// picks it up through its optional constructor parameter; without it no update is sent or stored. Idempotent.
    /// </summary>
    public static IServiceCollection AddGossipServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IChannelUpdateService, ChannelUpdateService>();
        return services;
    }
}