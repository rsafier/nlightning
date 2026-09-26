using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Onchain;

using Interfaces;

public static class OnchainServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BOLT 5 wiring of this layer (plan O2-T5): <see cref="IOnchainChannelWatcher"/> and
    /// <see cref="IOnchainResolutionExecutor"/>, singletons (TryAdd, so idempotent and replaceable), and
    /// <see cref="OnchainOptions"/>.
    /// </summary>
    /// <remarks>
    /// They need <c>AddChannelSafetyServices</c> (the error sender), <c>AddOnchainBitcoinServices</c> (the output
    /// mapper), the chain monitor's <c>IChainBroadcaster</c>/<c>IOutpointWatcher</c> and the repositories. Nothing to
    /// start: the channel manager hands them every funding spend, every resolution-output spend and every new block.
    /// The resolvers (<c>Domain.Onchain.Interfaces.IOutputResolver</c>) are registered by their own lanes, scoped or
    /// singleton; the executor resolves them per round from a scope.
    /// </remarks>
    public static IServiceCollection AddOnchainServices(this IServiceCollection services)
    {
        services.AddOptions<OnchainOptions>();
        services.TryAddSingleton<OnchainResolutionExecutor>();
        services.TryAddSingleton<IOnchainResolutionExecutor>(sp => sp.GetRequiredService<OnchainResolutionExecutor>());
        services.TryAddSingleton<OnchainChannelWatcher>();
        services.TryAddSingleton<IOnchainChannelWatcher>(sp => sp.GetRequiredService<OnchainChannelWatcher>());
        return services;
    }
}