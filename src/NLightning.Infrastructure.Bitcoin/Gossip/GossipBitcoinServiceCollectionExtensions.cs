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
    private static readonly TimeSpan s_connectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Registers the BOLT 7 chain and crypto services (G0-T3, G2-T2) as singletons (TryAdd, so idempotent):
    /// <see cref="IGossipSignatureVerifier"/> (<see cref="GossipSignatureVerifier"/>, stateless) and
    /// <see cref="IFundingOutputLookup"/> (<see cref="FundingOutputLookup"/>; needs <see cref="IBitcoinChainService"/>,
    /// and invalidates its cache on the <see cref="IOutpointWatcher"/>'s disconnected blocks when one is registered).
    /// Its limits come from <c>IOptions&lt;FundingOutputLookupOptions&gt;</c> and its txid source from
    /// <c>IOptions&lt;FundingTxIdSourceOptions&gt;</c>: the defaults (bitcoind), or bind both from the <c>Gossip</c>
    /// section with <c>Configure&lt;…&gt;</c>. With <c>Gossip:FundingTxIdSource = Esplora</c> an
    /// <see cref="EsploraTxIdSource"/> singleton (its own long-lived <see cref="HttpClient"/>) feeds the lookup.
    /// The lookup is registered as itself, as <see cref="IFundingOutputLookup"/> and as a Domain
    /// <see cref="IGossipPendingChannels"/> (NL-415) that always resolves the concrete singleton, so decorating
    /// <see cref="IFundingOutputLookup"/> keeps the pending view.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="esploraHandler">The HTTP handler of the Esplora client instead of the default one (tests); the
    /// source owns it.</param>
    public static IServiceCollection AddGossipBitcoinServices(
        this IServiceCollection services, Func<IServiceProvider, HttpMessageHandler>? esploraHandler = null)
    {
        services.AddOptions<FundingOutputLookupOptions>();
        services.AddOptions<FundingTxIdSourceOptions>();
        services.TryAddSingleton<IGossipSignatureVerifier, GossipSignatureVerifier>();
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<FundingTxIdSourceOptions>>();
            var handler = esploraHandler?.Invoke(sp)
                       ?? new SocketsHttpHandler { PooledConnectionLifetime = s_connectionLifetime };
            var httpClient = new HttpClient(handler) { Timeout = options.Value.EsploraTimeout };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("NLightning");
            return new EsploraTxIdSource(sp.GetRequiredService<IBitcoinChainService>(), httpClient,
                                         sp.GetRequiredService<ILogger<EsploraTxIdSource>>(), options,
                                         sp.GetService<TimeProvider>(), ownsHttpClient: true);
        });
        services.TryAddSingleton(sp =>
        {
            var source = sp.GetRequiredService<IOptions<FundingTxIdSourceOptions>>().Value;
            var errors = source.GetValidationErrors();
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    $"Invalid gossip funding txid source options: {string.Join("; ", errors)}");

            return new FundingOutputLookup(sp.GetRequiredService<IBitcoinChainService>(),
                                           sp.GetRequiredService<ILogger<FundingOutputLookup>>(),
                                           sp.GetRequiredService<IOptions<FundingOutputLookupOptions>>(),
                                           sp.GetService<TimeProvider>(), sp.GetService<IOutpointWatcher>(),
                                           source.FundingTxIdSource == FundingTxIdSourceKind.Esplora
                                               ? sp.GetRequiredService<EsploraTxIdSource>()
                                               : null);
        });
        services.TryAddSingleton<IFundingOutputLookup>(sp => sp.GetRequiredService<FundingOutputLookup>());
        // NL-415: the pending view is the concrete singleton, not whatever IFundingOutputLookup resolves to, so a
        // decorated lookup (the gossip probe's timing wrapper) still reports its in-flight and mempool-spent channels
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IGossipPendingChannels, FundingOutputLookup>(
                                      sp => sp.GetRequiredService<FundingOutputLookup>()));
        return services;
    }
}