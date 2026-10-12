using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;

/// <summary>
/// <c>bumpopen</c> (ClientCommand 38, lane dfrbf): RBF of our unconfirmed dual-funded open.
/// </summary>
public static class DualFundIpcServiceExtensions
{
    /// <summary>
    /// Registers the client handler (scoped, over <see cref="IDualFundedOpenService"/> from the Application's
    /// <c>AddDualFundingServices()</c>; a node without it answers <c>invalid_operation</c> "not available") and its IPC
    /// handler. Idempotent (every registration is a TryAdd).
    /// </summary>
    public static IServiceCollection AddDualFundIpcServices(this IServiceCollection services)
    {
        services.TryAddScoped<IClientCommandHandler<BumpOpenClientRequest, BumpOpenClientResponse>>(sp =>
            new BumpOpenClientHandler(sp.GetRequiredService<ILogger<BumpOpenClientHandler>>(),
                                      sp.GetService<IDualFundedOpenService>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, BumpOpenIpcHandler>());
        return services;
    }
}