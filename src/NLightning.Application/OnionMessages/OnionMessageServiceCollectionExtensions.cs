using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.OnionMessages;

using Domain.Channels.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.OnionMessages.Interfaces;
using Gossip.Graph.Interfaces;

/// <summary>
/// Registers BOLT 4 onion messages (wave M6, plan OM3-T1).
/// </summary>
public static class OnionMessageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OnionMessageService"/> as the <see cref="IOnionMessageService"/> singleton (the peer
    /// services hand it every <c>onion_message</c>) and <see cref="OnionMessageMetrics"/>. Bind
    /// <see cref="OnionMessageOptions"/> from <see cref="OnionMessageOptions.SectionName"/>; the defaults apply
    /// otherwise. Optional collaborators are taken when registered: the <see cref="IOnionMessagePacketBuilder"/>
    /// (without it the service stays off), the <see cref="IOnionMessageRateLimiter"/> (without it nothing is rate
    /// limited), the graph (<see cref="IGraphStore"/>, for paths beyond our peers) and every
    /// <see cref="IOnionMessageHandler"/> (register those as singletons).
    /// </summary>
    public static IServiceCollection AddOnionMessageServices(this IServiceCollection services)
    {
        services.AddOptions<OnionMessageOptions>();
        services.TryAddSingleton<OnionMessageMetrics>();
        services.TryAddSingleton(sp => new OnionMessageService(
                                     sp.GetRequiredService<IOptions<NodeOptions>>(),
                                     sp.GetRequiredService<ISecureKeyManager>(),
                                     sp.GetRequiredService<ISphinxService>(),
                                     sp.GetRequiredService<IRouteBlindingService>(),
                                     sp.GetRequiredService<IPeerManager>(),
                                     sp.GetRequiredService<IChannelMemoryRepository>(),
                                     sp.GetServices<IOnionMessageHandler>(),
                                     sp.GetRequiredService<OnionMessageMetrics>(),
                                     sp.GetRequiredService<ILogger<OnionMessageService>>(),
                                     sp.GetService<IOptions<OnionMessageOptions>>(),
                                     sp.GetService<IOnionMessagePacketBuilder>(),
                                     sp.GetService<IOnionMessageRateLimiter>(),
                                     sp.GetService<IGraphStore>(),
                                     sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IOnionMessageService>(sp => sp.GetRequiredService<OnionMessageService>());
        return services;
    }
}