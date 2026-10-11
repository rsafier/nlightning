using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure.Bitcoin.Onion.OnionMessages;

using Domain.Protocol.OnionMessages.Interfaces;
using RouteBlinding;

public static class OnionMessageCryptoServiceCollectionExtensions
{
    /// <summary>
    /// Registers the onion-message crypto (BOLT 4 "Onion Messages", wave M6 OM1) as stateless singletons:
    /// <see cref="IBlindedMessagePathBuilder"/>, <see cref="IOnionMessagePacketBuilder"/> and
    /// <see cref="IOnionMessageUnwrapper"/>. They need <c>ISphinxService</c> and <c>ISecp256K1Math</c> from
    /// <c>AddBitcoinInfrastructure</c>, and <see cref="Domain.Protocol.Onion.Interfaces.IRouteBlindingService"/>, which
    /// this adds if missing.
    /// </summary>
    public static IServiceCollection AddOnionMessageCryptoServices(this IServiceCollection services)
    {
        services.AddRouteBlindingServices();
        services.TryAddSingleton<IBlindedMessagePathBuilder, BlindedMessagePathBuilder>();
        services.TryAddSingleton<IOnionMessagePacketBuilder, OnionMessagePacketBuilder>();
        services.TryAddSingleton<IOnionMessageUnwrapper, OnionMessageUnwrapper>();
        return services;
    }
}