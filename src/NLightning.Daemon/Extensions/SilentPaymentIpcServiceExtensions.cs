using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;

public static class SilentPaymentIpcServiceExtensions
{
    public static IServiceCollection AddSilentPaymentIpcServices(this IServiceCollection services)
    {
        services.TryAddScoped<IClientCommandHandler<SilentPaymentClientRequest, SilentPaymentClientResponse>, SilentPaymentClientHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, GetSilentPaymentAddressIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, SilentPaymentLabelsIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, SilentPaymentRescanIpcHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, SilentPaymentStatusIpcHandler>());
        services.TryAddScoped<IClientCommandHandler<WalletHistoryClientRequest, WalletHistoryClientResponse>, WalletHistoryClientHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, WalletHistoryIpcHandler>());
        return services;
    }
}