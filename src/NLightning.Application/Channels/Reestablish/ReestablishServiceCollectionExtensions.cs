using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Reestablish;

using Domain.Channels.Reestablish;
using Interfaces;
using Services;

/// <summary>
/// Registers channel reestablishment (BOLT2 plan N7).
/// </summary>
public static class ReestablishServiceCollectionExtensions
{
    /// <summary>
    /// Adds the singleton <see cref="ReestablishTracker"/> (also as <see cref="IReestablishTracker"/>), the scoped
    /// <see cref="ReestablishService"/>, and the default <see cref="IPeerLivenessProbe"/>: the
    /// <see cref="ConnectedPeerLivenessProbe"/> gated by the tracker (<see cref="ReestablishGatedLivenessProbe"/>).
    /// Call it before <c>AddChannelOperationsServices</c>, whose probe registration is a <c>TryAdd</c>.
    /// </summary>
    public static IServiceCollection AddReestablishServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ReestablishTracker>();
        services.TryAddSingleton<IReestablishTracker>(sp => sp.GetRequiredService<ReestablishTracker>());
        services.TryAddScoped<ReestablishService>();
        services.TryAddSingleton<ConnectedPeerLivenessProbe>();
        services.TryAddSingleton<IPeerLivenessProbe>(sp => new ReestablishGatedLivenessProbe(
                                                         sp.GetRequiredService<ConnectedPeerLivenessProbe>(),
                                                         sp.GetRequiredService<ReestablishTracker>()));

        return services;
    }
}