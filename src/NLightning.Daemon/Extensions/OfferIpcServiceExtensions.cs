using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Offers.Interfaces;
using Handlers;
using Interfaces;

/// <summary>
/// The BOLT 12 offer commands <c>createoffer</c>, <c>listoffers</c> and <c>disableoffer</c> (provisional
/// ClientCommand 26-28, <c>OfferClientCommands</c>; wave B12 lane D).
/// </summary>
public static class OfferIpcServiceExtensions
{
    /// <summary>
    /// Registers the three client handlers (scoped; a node without an <see cref="IOfferService"/> answers
    /// "not available") and their IPC handlers. Idempotent (every registration is a TryAdd), so a second call cannot
    /// give the router a duplicate command. The offer service itself comes from the Application's
    /// <c>AddOffersServices()</c>.
    /// </summary>
    public static IServiceCollection AddOfferIpcServices(this IServiceCollection services)
    {
        services.TryAddScoped<IClientCommandHandler<CreateOfferClientRequest, CreateOfferClientResponse>>(sp =>
            new CreateOfferClientHandler(NodeServiceExtensions.GetPaymentLayerService<IOfferService>(sp),
                                         sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddScoped<IClientCommandHandler<ListOffersClientRequest, ListOffersClientResponse>>(sp =>
            new ListOffersClientHandler(NodeServiceExtensions.GetPaymentLayerService<IOfferService>(sp),
                                        sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddScoped<IClientCommandHandler<DisableOfferClientRequest, DisableOfferClientResponse>>(sp =>
            new DisableOfferClientHandler(NodeServiceExtensions.GetPaymentLayerService<IOfferService>(sp),
                                          sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, CreateOfferIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, ListOffersIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, DisableOfferIpcHandler>());

        return services;
    }
}