using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;

/// <summary>
/// The operator IPC commands of wave rf1 (NL-152 remainder): <c>disconnect</c> (ClientCommand 24).
/// </summary>
public static class OperatorIpcServiceExtensions
{
    /// <summary>
    /// Registers the <c>disconnect</c> client handler (scoped) and its IPC handler. Call once, from
    /// <see cref="NodeServiceExtensions.AddNltgNodeServices"/>: a second IPC handler for the same command crashes the
    /// router.
    /// </summary>
    public static IServiceCollection AddOperatorIpcServices(this IServiceCollection services)
    {
        services.AddScoped<IClientCommandHandler<DisconnectPeerClientRequest, DisconnectPeerClientResponse>,
            DisconnectPeerClientHandler>();
        services.AddSingleton<IIpcCommandHandler, DisconnectPeerIpcHandler>();

        return services;
    }
}