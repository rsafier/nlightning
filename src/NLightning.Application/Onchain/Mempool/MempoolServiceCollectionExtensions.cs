using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Onchain.Mempool;

using Domain.Channels.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Resolvers;

public static class MempoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MempoolReactor"/> (BOLT 5 plan O8, NL-098) as itself and <see cref="IMempoolReactor"/>
    /// (TryAdd singletons, idempotent). It needs <c>AddOnchainServices</c> (the watcher), the chain monitor and the
    /// repositories; the penalty resolver (<c>AddRevokedCommitResolver</c>) and <see cref="IBitcoinChainService"/> are
    /// optional. The host starts it before the chain monitor (<see cref="IMempoolReactor.Start"/>) and stops it before
    /// the monitor stops; <see cref="OnchainMempoolOptions"/> (<c>Node:Onchain:Mempool</c>) can turn it off.
    /// </summary>
    public static IServiceCollection AddOnchainMempoolServices(this IServiceCollection services)
    {
        services.AddOptions<OnchainOptions>();
        services.TryAddSingleton(sp => new MempoolReactor(sp.GetRequiredService<IBlockchainMonitor>(),
                                                          sp.GetRequiredService<IChannelLockProvider>(),
                                                          sp.GetRequiredService<IChannelMemoryRepository>(),
                                                          sp.GetRequiredService<ILogger<MempoolReactor>>(),
                                                          sp.GetRequiredService<IOptions<OnchainOptions>>(),
                                                          sp.GetRequiredService<IServiceScopeFactory>(),
                                                          sp.GetRequiredService<OnchainChannelWatcher>(),
                                                          sp.GetService<RevokedCommitResolver>(),
                                                          sp.GetService<IBitcoinChainService>()));
        services.TryAddSingleton<IMempoolReactor>(sp => sp.GetRequiredService<MempoolReactor>());
        return services;
    }
}