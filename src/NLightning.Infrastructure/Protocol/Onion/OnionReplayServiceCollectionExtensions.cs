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
    /// one is registered already (a persistent store registered first wins). For compositions without a database.
    /// </summary>
    public static IServiceCollection AddOnionReplayStore(this IServiceCollection services)
    {
        services.TryAddSingleton<IOnionReplayStore, InMemoryOnionReplayStore>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="IOnionReplayStore"/> as the database-backed <see cref="PersistentOnionReplayStore"/>
    /// (singleton), replacing any store registered before. It opens a scope per call and needs a Scoped
    /// <c>IUnitOfWork</c> (<c>AddRepositoriesInfrastructureServices</c>) by the time an onion is processed.
    /// </summary>
    public static IServiceCollection AddPersistentOnionReplayStore(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IOnionReplayStore, PersistentOnionReplayStore>());
        return services;
    }
}