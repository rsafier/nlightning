using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin;

using Builders;
using Builders.Interfaces;
using Crypto.Functions;
using Domain.Bitcoin.Interfaces;
using Domain.Crypto.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Infrastructure.Crypto.Interfaces;
using Onion;
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
                                            secureKeyManager, utxoMemoryRepository);
        });

        // Register Scoped Services
        services.AddScoped<IBitcoinWalletService, BitcoinWalletService>();

        return services;
    }
}