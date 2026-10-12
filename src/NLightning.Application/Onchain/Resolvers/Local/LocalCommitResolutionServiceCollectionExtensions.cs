using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Onchain.Resolvers.Local;

using Domain.Onchain.Interfaces;

public static class LocalCommitResolutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="LocalCommitResolver"/> as an <see cref="IOutputResolver"/> (BOLT 5 plan O3-T3/T4) and the
    /// wallet <see cref="ISweepDestinationProvider"/>, and <see cref="UnavailableAnchorFeeInputProvider"/> as the
    /// <see cref="IAnchorFeeInputProvider"/> unless the host registered the wallet's first (O7-T3). Idempotent.
    /// </summary>
    /// <remarks>
    /// Needs <c>ICommitmentOutputMapper</c>, <c>ISweepTransactionBuilder</c> (<c>AddOnchainBitcoinServices</c>),
    /// <c>IHtlcTransactionBuilder</c> and <c>ILightningSigner</c> (<c>AddBitcoinInfrastructure</c>), <c>IFeeService</c>,
    /// a scoped <c>IUnitOfWork</c> and <c>IBitcoinWalletService</c>; <c>IChannelMemoryRepository</c> and
    /// <c>IBlockchainMonitor</c> are used when registered, <c>IOptions&lt;LocalCommitResolverOptions&gt;</c> and a
    /// <c>SweepFeePolicy</c> singleton too.
    /// </remarks>
    public static IServiceCollection AddLocalCommitResolutionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISweepDestinationProvider, WalletSweepDestinationProvider>();
        services.TryAddSingleton<IAnchorFeeInputProvider, UnavailableAnchorFeeInputProvider>();
        services.TryAddSingleton<LocalCommitResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutputResolver, LocalCommitResolver>(
                                      sp => sp.GetRequiredService<LocalCommitResolver>()));
        return services;
    }
}