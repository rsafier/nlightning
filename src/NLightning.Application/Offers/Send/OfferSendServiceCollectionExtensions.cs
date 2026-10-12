using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Offers.Send;

using Domain.Channels.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Offers.Interfaces;
using Domain.Payments.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.OnionMessages.Interfaces;
using Gossip.Graph.Interfaces;
using OnionMessages;

/// <summary>
/// Registers the payer side of BOLT 12 offers (lane B12-E).
/// </summary>
public static class OfferSendServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="OfferPaymentService"/> as the <see cref="IOfferPaymentService"/> singleton (TryAdd,
    /// idempotent).
    /// </summary>
    /// <remarks>
    /// Needs <see cref="IOnionMessageService"/> (<c>AddOnionMessageServices</c>) and <see cref="IPaymentService"/>
    /// (<c>AddPaymentSendServices</c>). <see cref="IBolt12Signer"/> (lane B12-B, <c>AddBitcoinInfrastructure</c>) is
    /// optional: without it the service reports itself unavailable. With <see cref="IPeerManager"/>,
    /// <see cref="IChannelMemoryRepository"/> and <see cref="ISecureKeyManager"/> registered, invoice paths whose
    /// introduction node is a short channel id and direction are resolved through our channels and the graph
    /// (<see cref="OnionMessagePathFinder.Resolve"/>); otherwise such paths are not used.
    /// </remarks>
    public static IServiceCollection AddOfferSendServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOfferPaymentService>(sp => new OfferPaymentService(
                                                           sp.GetRequiredService<IOnionMessageService>(),
                                                           sp.GetRequiredService<IPaymentService>(),
                                                           sp.GetRequiredService<IOptions<NodeOptions>>(),
                                                           sp.GetRequiredService<ILogger<OfferPaymentService>>(),
                                                           sp.GetRequiredService<TimeProvider>(),
                                                           sp.GetService<IBolt12Signer>(),
                                                           CreatePathFinder(sp)));
        return services;
    }

    private static OnionMessagePathFinder? CreatePathFinder(IServiceProvider provider)
    {
        if (provider.GetService<IPeerManager>() is not { } peerManager
         || provider.GetService<IChannelMemoryRepository>() is not { } channels
         || provider.GetService<ISecureKeyManager>() is not { } keyManager)
            return null;

        return new OnionMessagePathFinder(peerManager, channels, keyManager.GetNodePubKey(), 3,
                                          provider.GetService<IGraphStore>());
    }
}