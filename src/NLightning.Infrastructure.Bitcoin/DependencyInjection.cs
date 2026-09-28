using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin;

using Bootstrap;
using Builders;
using Builders.Interfaces;
using Crypto.Functions;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Crypto.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Offers.Interfaces;
using Domain.Onchain.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Gossip;
using Infrastructure.Crypto.Interfaces;
using InteractiveTx;
using Offers;
using Onion;
using Onion.OnionMessages;
using Onion.RouteBlinding;
using Services;
using Signers;
using Wallet;
using Wallet.Interfaces;

/// <summary>
/// Extension methods for setting up Bitcoin infrastructure services in an IServiceCollection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds Bitcoin infrastructure services to the specified IServiceCollection.
    /// </summary>
    /// <param name="services">The IServiceCollection to add services to.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddBitcoinInfrastructure(this IServiceCollection services)
    {
        // Register Singletons
        services.AddSingleton<IBitcoinChainService, BitcoinChainService>();
        services.AddSingleton<IBlockchainMonitor, BlockchainMonitorService>();

        // The monitor is also the broadcaster and the outpoint watcher (BOLT 5 plan O0; Domain ports)
        services.AddSingleton<IChainBroadcaster>(sp => sp.GetRequiredService<IBlockchainMonitor>());
        services.AddSingleton<IOutpointWatcher>(sp => sp.GetRequiredService<IBlockchainMonitor>());

        services.AddSingleton<IClosingTransactionBuilder, ClosingTransactionBuilder>();
        services.AddSingleton<ICommitmentKeyDerivationService, CommitmentKeyDerivationService>();
        services.AddSingleton<ICommitmentTransactionBuilder, CommitmentTransactionBuilder>();
        services.AddSingleton<IDustService, DustService>(); // needs IFeeService, registered by the host
        services.AddSingleton<IEcdh, Ecdh>();
        services.AddSingleton<IFailureOnionService, FailureOnionService>(); // needs IFailureMessageSerializer (Serialization)
        services.AddSingleton<IFundingOutputBuilder, FundingOutputBuilder>();
        services.AddSingleton<IFundingTransactionBuilder, FundingTransactionBuilder>();
        services.AddSingleton<IHtlcTransactionBuilder, HtlcTransactionBuilder>();
        services.AddSingleton<IKeyDerivationService, KeyDerivationService>();
        services.AddSingleton<IPerCommitmentSecretVerifier, PerCommitmentSecretVerifier>();
        services.AddSingleton<ISecp256K1Math, Secp256K1Math>();
        services.AddSingleton<ISphinxService, SphinxService>();

        // BOLT 4 attributable failures and hold times (onion M3b); the switch uses it when OptionAttributionData is advertised
        services.AddOnionAttributionServices();

        // BOLT 4 route blinding (onion M5); the switch reads blinded payloads when OptionRouteBlinding is advertised
        services.AddRouteBlindingServices();

        // BOLT 4 onion messages (wave M6 OM1): message paths, the packet builder and the receive-side unwrapper
        services.AddOnionMessageCryptoServices();

        // BOLT 7 gossip signature verification and the funding output lookup of channel announcements (G0-T3, G2-T2)
        services.AddGossipBitcoinServices();

        // BOLT 2 interactive transaction construction (splicing plan IT2): prevtx inspector, builder, wallet prevtx source
        services.AddInteractiveTxBitcoinServices();

        // The signer holds the node's secrets; ISecureKeyManager is registered by the host
        services.AddSingleton<ILightningSigner>(sp =>
        {
            var fundingOutputBuilder = sp.GetRequiredService<IFundingOutputBuilder>();
            var keyDerivationService = sp.GetRequiredService<IKeyDerivationService>();
            var logger = sp.GetRequiredService<ILogger<LocalLightningSigner>>();
            var nodeOptions = sp.GetRequiredService<IOptions<NodeOptions>>().Value;
            var secureKeyManager = sp.GetRequiredService<ISecureKeyManager>();
            var utxoMemoryRepository = sp.GetRequiredService<IUtxoMemoryRepository>();
            return new LocalLightningSigner(fundingOutputBuilder, keyDerivationService, logger, nodeOptions,
                                            secureKeyManager, utxoMemoryRepository,
                                            sp.GetService<IChannelSigningInfoSource>());
        });

        // BOLT 12 BIP-340 signatures; the keys stay in ILightningSigner (BOLT 12 plan B1)
        services.AddSingleton<IBolt12Signer, Bolt12Signer>();

        // Fee inputs for CPFP and anchor HTLC transactions (BOLT 5 plan O7-T1)
        services.AddSingleton<IFeeInputSelector, FeeInputSelector>();

        // The wallet reserve of anchors channels and the channel funding selection that keeps it (NL-379, NL-385)
        services.AddSingleton<IAnchorReserveService, AnchorReserveService>();

        // BOLT 10 DNS seed client (NL-113), over IDnsRecordLookup (AddInfrastructureServices); PeerBootstrapService
        // is its only user and does nothing unless Node:Bootstrap:Enabled
        services.AddSingleton<IDnsSeedClient, DnsSeedClient>();

        // Register Scoped Services
        services.AddScoped<IBitcoinWalletService, BitcoinWalletService>();

        return services;
    }
}