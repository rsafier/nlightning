using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Quiescence;

using Domain.Channels.Quiescence;

/// <summary>
/// Registers quiescence (BOLT 2 "Channel Quiescence"; splicing plan wave Q, lane Q-B).
/// </summary>
public static class QuiescenceServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="QuiescenceService"/> as itself, <see cref="IQuiescenceService"/> and <see cref="IStfuReleaseScheduler"/>
    /// (one instance), <see cref="QuiescenceTimeoutMonitor"/> (started by the service with the first quiescence) and the
    /// default <see cref="IQuiescencePeerDisconnector"/>. Idempotent (TryAdd). Nothing for the host to start; the
    /// options are <c>Node:Quiescence</c> (<c>NodeOptions.Quiescence</c>).
    /// </summary>
    public static IServiceCollection AddQuiescenceServices(this IServiceCollection services)
    {
        services.TryAddSingleton<QuiescenceService>();
        services.TryAddSingleton<IQuiescenceService>(sp => sp.GetRequiredService<QuiescenceService>());
        services.TryAddSingleton<IStfuReleaseScheduler>(sp => sp.GetRequiredService<QuiescenceService>());
        services.TryAddSingleton<IQuiescencePeerDisconnector, PeerServiceQuiescenceDisconnector>();
        services.TryAddSingleton<QuiescenceTimeoutMonitor>();
        return services;
    }
}