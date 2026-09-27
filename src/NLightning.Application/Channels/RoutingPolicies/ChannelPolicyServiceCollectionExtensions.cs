using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.RoutingPolicies;

using Domain.Channels.RoutingPolicies;

public static class ChannelPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-channel routing policies (wave sp1 lane SP1-G): <see cref="ChannelPolicyStore"/> (itself and
    /// <see cref="IChannelPolicyProvider"/>, one instance) and <see cref="IChannelPolicyService"/>. The forwarding
    /// policy and the channel update service pick the provider up through optional constructor arguments; without this
    /// registration every channel uses <c>Node:Routing</c>. Idempotent (every registration is a TryAdd).
    /// </summary>
    public static IServiceCollection AddChannelPolicyServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ChannelPolicyStore>();
        services.TryAddSingleton<IChannelPolicyProvider>(sp => sp.GetRequiredService<ChannelPolicyStore>());
        services.TryAddSingleton<IChannelPolicyService, ChannelPolicyService>();
        return services;
    }
}