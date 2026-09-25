using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin;

using Builders;
using Builders.Interfaces;
using Crypto.Functions;
using Domain.Crypto.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Infrastructure.Crypto.Interfaces;
using Onion;
using Services;
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
        services.AddSingleton<ICommitmentKeyDerivationService, CommitmentKeyDerivationService>();
        services.AddSingleton<ICommitmentTransactionBuilder, CommitmentTransactionBuilder>();
        services.AddSingleton<IDustService, DustService>(); // needs IFeeService, registered by the host
        services.AddSingleton<IEcdh, Ecdh>();
        services.AddSingleton<IFundingOutputBuilder, FundingOutputBuilder>();
        services.AddSingleton<IFundingTransactionBuilder, FundingTransactionBuilder>();
        services.AddSingleton<IHtlcTransactionBuilder, HtlcTransactionBuilder>();
        services.AddSingleton<IKeyDerivationService, KeyDerivationService>();
        services.AddSingleton<IPerCommitmentSecretVerifier, PerCommitmentSecretVerifier>();
        services.AddSingleton<ISecp256K1Math, Secp256K1Math>();
        services.AddSingleton<ISphinxService, SphinxService>();

        // Register Scoped Services
        services.AddScoped<IBitcoinWalletService, BitcoinWalletService>();

        return services;
    }
}