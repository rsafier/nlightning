using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Node.Options;
using Interfaces;

public static class WalletSpendServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IWalletSpendService"/> (the on-chain <c>withdraw</c>) as a singleton, once. It needs the
    /// services of <c>AddBitcoinInfrastructure</c> (fee input selector, anchors reserve, signer, chain monitor), the
    /// host's <see cref="IFeeService"/> and the persistence layer's <c>IUnitOfWork</c>.
    /// </summary>
    public static IServiceCollection AddWalletSpendServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IWalletSpendService>(sp => new WalletSpendService(
                                                          sp.GetRequiredService<IFeeInputSelector>(),
                                                          sp.GetRequiredService<IAnchorReserveService>(),
                                                          sp.GetRequiredService<IUtxoMemoryRepository>(),
                                                          sp.GetRequiredService<ILightningSigner>(),
                                                          sp.GetRequiredService<IBlockchainMonitor>(),
                                                          sp.GetRequiredService<IFeeService>(),
                                                          sp.GetRequiredService<IServiceScopeFactory>(),
                                                          sp.GetRequiredService<IOptions<NodeOptions>>(),
                                                          sp.GetRequiredService<ILogger<WalletSpendService>>(),
                                                          sp.GetService<IBitcoinChainService>(),
                                                          sp.GetRequiredService<IWalletPsbtService>(),
                                                          sp.GetService<IOptions<SilentPaymentsOptions>>(),
                                                          sp.GetService<ISilentPaymentCrypto>(),
                                                          sp.GetService<ISilentPaymentKeySource>()));
        // The walletrpc PSBT and lease surface (LND gRPC wave 3, NL-1184) goes with withdraw
        services.TryAddSingleton<IWalletPsbtService>(sp => new WalletPsbtService(
                                                         sp.GetRequiredService<IFeeInputSelector>(),
                                                         sp.GetRequiredService<IAnchorReserveService>(),
                                                         sp.GetRequiredService<IUtxoMemoryRepository>(),
                                                         sp.GetRequiredService<ILightningSigner>(),
                                                         sp.GetRequiredService<IBlockchainMonitor>(),
                                                         sp.GetRequiredService<IServiceScopeFactory>(),
                                                         sp.GetRequiredService<IOptions<NodeOptions>>(),
                                                         sp.GetRequiredService<ILogger<WalletPsbtService>>(),
                                                         sp.GetService<IBitcoinChainService>(),
                                                         sp.GetService<TimeProvider>(),
                                                         sp.GetService<Domain.Protocol.Interfaces.ISecureKeyManager>()));
        return services;
    }
}