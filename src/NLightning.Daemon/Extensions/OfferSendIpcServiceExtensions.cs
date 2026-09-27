using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Daemon.Extensions;

using Application.Offers.Send;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;

/// <summary>
/// The payer side of BOLT 12 offers over IPC: <c>payoffer</c> and <c>fetchinvoice</c> (ClientCommand 29 and 30).
/// </summary>
public static class OfferSendIpcServiceExtensions
{
    /// <summary>
    /// Registers the offer payment service (<c>AddOfferSendServices</c>), the two client handlers (scoped) and their IPC
    /// handlers. Idempotent (every registration is a TryAdd).
    /// </summary>
    public static IServiceCollection AddOfferSendIpcServices(this IServiceCollection services)
    {
        services.AddOfferSendServices();
        services.TryAddScoped<IClientCommandHandler<PayOfferClientRequest, PayOfferClientResponse>,
            PayOfferClientHandler>();
        services.TryAddScoped<IClientCommandHandler<PayOfferClientRequest, FetchInvoiceClientResponse>,
            FetchInvoiceClientHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, PayOfferIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, FetchInvoiceIpcHandler>());

        return services;
    }
}