using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Close;

using Domain.Channels.Interfaces;

public static class CloseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mutual close (BOLT2 plan N10): the singletons <see cref="ClosingNegotiationRegistry"/>,
    /// <see cref="ClosingFeeEstimator"/> (needs the host's <c>IFeeService</c>),
    /// <see cref="ClosingTimeoutMonitor"/> (fails through the <c>IChannelFailureService</c> of
    /// <c>AddChannelSafetyServices</c>, resolved when a deadline passes) and <see cref="IChannelCloseService"/>, the scoped <see cref="ChannelCloseCoordinator"/> and
    /// <see cref="ShutdownScriptProvider"/>, the scoped <see cref="Simple.SimpleCloseCoordinator"/>
    /// (<c>option_simple_close</c>, N11), and <see cref="ChannelCloseOptions"/> (defaults unless the host binds
    /// <c>Node:Close</c>). Idempotent. The <c>shutdown</c>/<c>closing_signed</c>/<c>closing_complete</c>/
    /// <c>closing_sig</c> handlers are registered by the handler scan.
    /// </summary>
    public static IServiceCollection AddChannelCloseServices(this IServiceCollection services)
    {
        services.AddOptions<ChannelCloseOptions>();
        services.TryAddSingleton<ClosingNegotiationRegistry>();
        services.TryAddSingleton<ClosingTimeoutMonitor>();
        services.TryAddSingleton<ClosingFeeEstimator>();
        services.TryAddSingleton<IChannelCloseService, ChannelCloseService>();
        services.TryAddScoped<ChannelCloseCoordinator>();
        services.TryAddScoped<Simple.SimpleCloseCoordinator>();
        services.TryAddScoped<ShutdownScriptProvider>();
        return services;
    }
}