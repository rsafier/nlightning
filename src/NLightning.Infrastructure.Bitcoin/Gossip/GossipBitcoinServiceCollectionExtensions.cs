using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Gossip;

using Domain.Gossip.Interfaces;
using Domain.Onchain.Interfaces;
using Wallet.Interfaces;

public static class GossipBitcoinServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BOLT 7 chain and crypto services (G0-T3, G2-T2) as singletons (TryAdd, so idempotent):
    /// <see cref="IGossipSignatureVerifier"/> (<see cref="GossipSignatureVerifier"/>, stateless) and
    /// <see cref="IFundingOutputLookup"/> (<see cref="FundingOutputLookup"/>; needs <see cref="IBitcoinChainService"/>,
    /// and invalidates its cache on the <see cref="IOutpointWatcher"/>'s disconnected blocks when one is registered).
    /// Its limits come from <c>IOptions&lt;FundingOutputLookupOptions&gt;</c>: the defaults, or bind them from the
    /// <c>Gossip</c> section with <c>Configure&lt;FundingOutputLookupOptions&gt;</c>.
    /// </summary>
    public static IServiceCollection AddGossipBitcoinServices(this IServiceCollection services)
    {
        services.AddOptions<FundingOutputLookupOptions>();
        services.TryAddSingleton<IGossipSignatureVerifier, GossipSignatureVerifier>();
        services.TryAddSingleton<IFundingOutputLookup>(sp => new FundingOutputLookup(
                                                           sp.GetRequiredService<IBitcoinChainService>(),
                                                           sp.GetRequiredService<ILogger<FundingOutputLookup>>(),
                                                           sp.GetRequiredService<
                                                               IOptions<FundingOutputLookupOptions>>(),
                                                           sp.GetService<TimeProvider>(),
                                                           sp.GetService<IOutpointWatcher>()));
        return services;
    }
}