using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure.Bitcoin.Onion.RouteBlinding;

using Domain.Protocol.Onion.Interfaces;

public static class RouteBlindingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IRouteBlindingService"/> (BOLT 4 route blinding, onion M5) as a stateless singleton. It
    /// needs <c>ISecp256K1Math</c> from <c>AddBitcoinInfrastructure</c> and, to read blinded payloads as the local node,
    /// the host's <c>ISecureKeyManager</c>.
    /// </summary>
    public static IServiceCollection AddRouteBlindingServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IRouteBlindingService, RouteBlindingService>();
        return services;
    }
}