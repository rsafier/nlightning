using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;
using Services;

/// <summary>
/// The operator IPC commands: <c>disconnect</c> (ClientCommand 24, wave rf1, NL-152 remainder) and <c>shutdown</c>
/// (ClientCommand 39, NL-591).
/// </summary>
public static class OperatorIpcServiceExtensions
{
    /// <summary>
    /// Registers the <c>disconnect</c> and <c>shutdown</c> client handlers (scoped), their IPC handlers and the
    /// <see cref="NodeShutdownTrigger"/> the IPC server stops the host with. Call once, from
    /// <see cref="NodeServiceExtensions.AddNltgNodeServices"/>: a second IPC handler for the same command crashes the
    /// router.
    /// </summary>
    public static IServiceCollection AddOperatorIpcServices(this IServiceCollection services)
    {
        services.AddScoped<IClientCommandHandler<DisconnectPeerClientRequest, DisconnectPeerClientResponse>,
            DisconnectPeerClientHandler>();
        services.AddSingleton<IIpcCommandHandler, DisconnectPeerIpcHandler>();

        services.AddSingleton(sp => new NodeShutdownTrigger(sp.GetService<IHostApplicationLifetime>()));
        services.AddScoped<IClientCommandHandler<ShutdownClientRequest, ShutdownClientResponse>,
            ShutdownClientHandler>();
        services.AddSingleton<IIpcCommandHandler, ShutdownIpcHandler>();

        return services;
    }
}