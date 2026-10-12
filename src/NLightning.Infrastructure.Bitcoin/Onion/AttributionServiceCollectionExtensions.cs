using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Protocol.Onion.Interfaces;

public static class AttributionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAttributionDataService"/> (BOLT 4 attributable failures, hold times and the
    /// <c>fulfillment_payload</c>) as a stateless singleton. It needs <see cref="IFailureOnionService"/> from
    /// <c>AddBitcoinInfrastructure</c>, which in turn needs <c>IFailureMessageSerializer</c> from
    /// <c>AddSerializationInfrastructureServices</c>.
    /// </summary>
    public static IServiceCollection AddOnionAttributionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IAttributionDataService, AttributionDataService>();
        return services;
    }
}