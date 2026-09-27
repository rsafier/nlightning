using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;

/// <summary>
/// Spontaneous (keysend) payments over IPC: <c>keysend</c> (ClientCommand 31, lane lh1-l3).
/// </summary>
public static class KeysendIpcServiceExtensions
{
    /// <summary>
    /// Registers the keysend client handler (scoped, over <c>IPaymentService</c> from <c>AddPaymentSendServices</c>) and
    /// its IPC handler. Idempotent (every registration is a TryAdd).
    /// </summary>
    public static IServiceCollection AddKeysendIpcServices(this IServiceCollection services)
    {
        services.TryAddScoped<IClientCommandHandler<KeysendClientRequest, PayInvoiceClientResponse>,
            KeysendClientHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, KeysendIpcHandler>());

        return services;
    }
}