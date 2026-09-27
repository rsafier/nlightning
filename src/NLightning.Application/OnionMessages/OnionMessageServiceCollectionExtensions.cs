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
    /// services hand it every <c>onion_message</c>) and <see cref="OnionMessageMetrics"/>. It needs the
    /// <see cref="IOnionMessageUnwrapper"/> and <see cref="IBlindedMessagePathBuilder"/> of
    /// <c>AddOnionMessageCryptoServices</c> (Infrastructure.Bitcoin, part of <c>AddBitcoinInfrastructure</c>). Bind
    /// <see cref="OnionMessageOptions"/> from <see cref="OnionMessageOptions.SectionName"/>; the defaults apply
    /// otherwise (invalid options keep the service off with an error log, they never fail its resolution). Optional
    /// collaborators are taken when registered: the <see cref="IOnionMessagePacketBuilder"/> and the
    /// <see cref="IPeerOnionMessageOutbox"/> send path (without either the service stays off), the <see cref="IOnionMessageRateLimiter"/> (without it nothing is rate
    /// limited), the graph (<see cref="IGraphStore"/>, for paths beyond our peers) and every
    /// <see cref="IOnionMessageHandler"/> (register those as singletons).
    /// </summary>
    public static IServiceCollection AddOnionMessageServices(this IServiceCollection services)
    {
        services.AddOptions<OnionMessageOptions>();
        services.TryAddSingleton<OnionMessageMetrics>();
        // M6 OM2-T2: the per-peer and node-wide token buckets, from the OnionMessages section
        services.TryAddSingleton<IOnionMessageRateLimiter>(sp =>
        {
            var o = sp.GetService<IOptions<OnionMessageOptions>>()?.Value ?? new OnionMessageOptions();
            var limits = new OnionMessageRateLimits(o.PeerBytesPerSecond, o.PeerBurstBytes, o.PeerMessagesPerSecond,
                                                    o.PeerBurstMessages, o.GlobalBytesPerSecond, o.GlobalBurstBytes,
                                                    o.GlobalMessagesPerSecond, o.GlobalBurstMessages);
            return new OnionMessageRateLimiter(limits, sp.GetService<TimeProvider>());
        });
        services.TryAddSingleton(sp => new OnionMessageService(
                                     sp.GetRequiredService<IOptions<NodeOptions>>(),
                                     sp.GetRequiredService<ISecureKeyManager>(),
                                     sp.GetRequiredService<IOnionMessageUnwrapper>(),
                                     sp.GetRequiredService<IBlindedMessagePathBuilder>(),
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
                                     sp.GetService<TimeProvider>(),
                                     sp.GetService<IPeerOnionMessageOutbox>()));
        services.TryAddSingleton<IOnionMessageService>(sp => sp.GetRequiredService<OnionMessageService>());
        return services;
    }
}