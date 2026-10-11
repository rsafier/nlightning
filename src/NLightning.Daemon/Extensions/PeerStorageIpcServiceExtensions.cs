using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.PeerStorage;
using Handlers;
using Interfaces;

/// <summary>
/// The <c>listpeerstorage</c> command (ClientCommand 32, NL-432).
/// </summary>
public static class PeerStorageIpcServiceExtensions
{
    /// <summary>
    /// Registers the <c>listpeerstorage</c> client handler (scoped; a node without <see cref="IPeerStorageService"/>
    /// answers "not available") and its IPC handler. Idempotent (every registration is a TryAdd).
    /// </summary>
    public static IServiceCollection AddPeerStorageIpcServices(this IServiceCollection services)
    {
        services.TryAddScoped<IClientCommandHandler<ListPeerStorageClientRequest, ListPeerStorageClientResponse>>(
            sp => new ListPeerStorageClientHandler(sp.GetService<IPeerStorageService>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, ListPeerStorageIpcHandler>());

        return services;
    }
}