using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Daemon.Extensions;

using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Infrastructure.Bitcoin.Wallet;
using Interfaces;

/// <summary>
/// The on-chain <c>withdraw</c> command (ClientCommand 25).
/// </summary>
public static class WithdrawIpcServiceExtensions
{
    /// <summary>
    /// Registers the wallet spend service, the <c>withdraw</c> client handler (scoped) and its IPC handler. Idempotent
    /// (every registration is a TryAdd), so a second call cannot give the router a duplicate command.
    /// </summary>
    public static IServiceCollection AddWithdrawIpcServices(this IServiceCollection services)
    {
        services.AddWalletSpendServices();
        services.TryAddScoped<IClientCommandHandler<WithdrawClientRequest, WithdrawClientResponse>,
            WithdrawClientHandler>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, WithdrawIpcHandler>());

        return services;
    }
}