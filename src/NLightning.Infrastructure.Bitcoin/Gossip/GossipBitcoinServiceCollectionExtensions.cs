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
    /// <c>Gossip</c> section with <c>Configure&lt;FundingOutputLookupOptions&gt;</c>. The lookup is registered as
    /// itself, as <see cref="IFundingOutputLookup"/> and as a Domain <see cref="IGossipPendingChannels"/> (NL-415) that
    /// always resolves the concrete singleton, so decorating <see cref="IFundingOutputLookup"/> keeps the pending view.
    /// </summary>
    public static IServiceCollection AddGossipBitcoinServices(this IServiceCollection services)
    {
        services.AddOptions<FundingOutputLookupOptions>();
        services.TryAddSingleton<IGossipSignatureVerifier, GossipSignatureVerifier>();
        services.TryAddSingleton(sp => new FundingOutputLookup(sp.GetRequiredService<IBitcoinChainService>(),
                                                               sp.GetRequiredService<ILogger<FundingOutputLookup>>(),
                                                               sp.GetRequiredService<
                                                                   IOptions<FundingOutputLookupOptions>>(),
                                                               sp.GetService<TimeProvider>(),
                                                               sp.GetService<IOutpointWatcher>()));
        services.TryAddSingleton<IFundingOutputLookup>(sp => sp.GetRequiredService<FundingOutputLookup>());
        // NL-415: the pending view is the concrete singleton, not whatever IFundingOutputLookup resolves to, so a
        // decorated lookup (the gossip probe's timing wrapper) still reports its in-flight and mempool-spent channels
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGossipPendingChannels, FundingOutputLookup>(
                                      sp => sp.GetRequiredService<FundingOutputLookup>()));
        return services;
    }
}