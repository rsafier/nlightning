using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Bitcoin.InteractiveTx;

using Domain.Protocol.InteractiveTx.Interfaces;
using Wallet.Interfaces;

/// <summary>
/// Registers the Bitcoin side of interactive transaction construction (splicing plan IT2): the <c>prevtx</c>
/// inspector, the transaction builder and the source of our wallet inputs' previous transactions.
/// </summary>
public static class InteractiveTxBitcoinServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IPrevTxInspector"/>, <see cref="IInteractiveTxBuilder"/> and <see cref="IWalletPrevTxSource"/>
    /// as singletons (TryAdd). The inspector's confirmation check and the prevtx source use
    /// <see cref="IBitcoinChainService"/>; the inspector works without it (every input then reads as unconfirmed).
    /// </summary>
    public static IServiceCollection AddInteractiveTxBitcoinServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IPrevTxInspector>(sp =>
            new PrevTxInspector(sp.GetService<ILogger<PrevTxInspector>>(), sp.GetService<IBitcoinChainService>()));
        services.TryAddSingleton<IInteractiveTxBuilder, InteractiveTxBuilder>();
        services.TryAddSingleton<IWalletPrevTxSource, ChainWalletPrevTxSource>();

        return services;
    }
}