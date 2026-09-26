using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure.Protocol.Onion;

using Domain.Protocol.Onion.Interfaces;

/// <summary>
/// Registers the onion replay set (NL-078).
/// </summary>
public static class OnionReplayServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IOnionReplayStore"/> as the in-memory <see cref="InMemoryOnionReplayStore"/> (singleton), unless
    /// one is registered already (a persistent store registered first wins).
    /// </summary>
    public static IServiceCollection AddOnionReplayStore(this IServiceCollection services)
    {
        services.TryAddSingleton<IOnionReplayStore, InMemoryOnionReplayStore>();
        return services;
    }
}