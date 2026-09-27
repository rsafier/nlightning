using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Daemon.Extensions;

using Application.Channels.RoutingPolicies;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.RoutingPolicies;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;

/// <summary>
/// The per-channel routing policy commands (wave sp1 lane SP1-G): <c>setchannelpolicy</c> (ClientCommand 35) and
/// <c>getchannelpolicy</c> (36).
/// </summary>
public static class ChannelPolicyIpcServiceExtensions
{
    /// <summary>
    /// Registers the channel policy services
    /// (<see cref="ChannelPolicyServiceCollectionExtensions.AddChannelPolicyServices"/>), both client handlers (scoped;
    /// without an <see cref="IChannelPolicyService"/> they answer "not available") and their IPC handlers. Idempotent
    /// (every registration is a TryAdd).
    /// </summary>
    public static IServiceCollection AddChannelPolicyIpcServices(this IServiceCollection services)
    {
        services.AddChannelPolicyServices();
        services.TryAddScoped<IClientCommandHandler<SetChannelPolicyClientRequest, ChannelPolicyClientResponse>>(
            sp => new SetChannelPolicyClientHandler(sp.GetRequiredService<IChannelMemoryRepository>(),
                                                    sp.GetService<IChannelPolicyService>(),
                                                    sp.GetService<IChannelPolicyProvider>()));
        services.TryAddScoped<IClientCommandHandler<GetChannelPolicyClientRequest, ChannelPolicyClientResponse>>(
            sp => new GetChannelPolicyClientHandler(sp.GetRequiredService<IChannelMemoryRepository>(),
                                                    sp.GetService<IChannelPolicyService>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, SetChannelPolicyIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, GetChannelPolicyIpcHandler>());

        return services;
    }
}