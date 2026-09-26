using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Close;

using Domain.Channels.Interfaces;

public static class CloseServiceCollectionExtensions
{
    /// <summary>
    /// Registers the mutual close (BOLT2 plan N10): the singleton <see cref="ClosingNegotiationRegistry"/> and
    /// <see cref="IChannelCloseService"/>, the scoped <see cref="ChannelCloseCoordinator"/> and
    /// <see cref="ShutdownScriptProvider"/>, and <see cref="ChannelCloseOptions"/> (defaults unless the host binds
    /// <c>Node:Close</c>). Idempotent. The <c>shutdown</c>/<c>closing_signed</c> handlers are registered by the handler
    /// scan.
    /// </summary>
    public static IServiceCollection AddChannelCloseServices(this IServiceCollection services)
    {
        services.AddOptions<ChannelCloseOptions>();
        services.TryAddSingleton<ClosingNegotiationRegistry>();
        services.TryAddSingleton<IChannelCloseService, ChannelCloseService>();
        services.TryAddScoped<ChannelCloseCoordinator>();
        services.TryAddScoped<ShutdownScriptProvider>();
        return services;
    }
}