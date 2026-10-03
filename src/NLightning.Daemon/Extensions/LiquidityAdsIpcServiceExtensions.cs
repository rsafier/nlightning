using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Daemon.Extensions;

using Application.Gossip.Graph.Interfaces;
using Application.LiquidityAds;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Handlers;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Interfaces;

/// <summary>
/// <c>liquidityads rates|sellers|purchases</c> (ClientCommand 46, liquidity ads NL-850).
/// </summary>
public static class LiquidityAdsIpcServiceExtensions
{
    /// <summary>
    /// Registers the client handler (scoped, over the Application's <see cref="LiquidityAdsService"/>; a node without it
    /// answers <c>invalid_operation</c> "not available"; every other collaborator is optional) and its IPC handler.
    /// Idempotent (every registration is a TryAdd).
    /// </summary>
    public static IServiceCollection AddLiquidityAdsIpcServices(this IServiceCollection services)
    {
        services.TryAddScoped<IClientCommandHandler<LiquidityAdsClientRequest, LiquidityAdsClientResponse>>(sp =>
            new LiquidityAdsClientHandler(sp.GetService<LiquidityAdsService>(), sp.GetService<IPeerManager>(),
                                          sp.GetService<IGraphStore>(), sp.GetService<IUnitOfWork>(),
                                          sp.GetService<IBlockchainMonitor>(), sp.GetService<ISecureKeyManager>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IIpcCommandHandler, LiquidityAdsIpcHandler>());
        return services;
    }
}