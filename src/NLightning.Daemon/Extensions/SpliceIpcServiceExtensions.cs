using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Channels.Splicing.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;

/// <summary>
/// The splice commands <c>splicein</c> and <c>spliceout</c> (ClientCommand 33/34; splicing plan §3.10, wave SP1 lane
/// SP1-E).
/// </summary>
public static class SpliceIpcServiceExtensions
{
    /// <summary>
    /// Registers the two client handlers (scoped, over <see cref="ISpliceService"/> from the Application's
    /// <c>AddSpliceServices()</c>; a node without it answers <c>invalid_operation</c> "not available") and their IPC
    /// handlers. Idempotent (every registration is a TryAdd), so a second call cannot give the router a duplicate
    /// command.
    /// </summary>
    public static IServiceCollection AddSpliceIpcServices(this IServiceCollection services)
    {
        services.TryAddScoped<IClientCommandHandler<SpliceInClientRequest, SpliceClientResponse>>(sp =>
            new SpliceInClientHandler(NodeServiceExtensions.GetPaymentLayerService<ISpliceService>(sp),
                                      sp.GetRequiredService<ILogger<SpliceInClientHandler>>()));
        services.TryAddScoped<IClientCommandHandler<SpliceOutClientRequest, SpliceClientResponse>>(sp =>
            new SpliceOutClientHandler(NodeServiceExtensions.GetPaymentLayerService<ISpliceService>(sp),
                                       sp.GetRequiredService<ILogger<SpliceOutClientHandler>>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, SpliceInIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, SpliceOutIpcHandler>());

        return services;
    }
}