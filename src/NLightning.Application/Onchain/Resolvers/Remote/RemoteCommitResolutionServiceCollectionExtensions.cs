using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Onchain.Interfaces;

public static class RemoteCommitResolutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RemoteCommitResolver"/> as an <see cref="IOutputResolver"/> (BOLT 5 plan O4), its wallet
    /// <see cref="IRemoteSweepDestination"/> and its <see cref="IRemoteCommitmentSource"/> (all singletons: every
    /// database access opens its own scope) and <see cref="RemoteResolutionOptions"/>. Idempotent.
    /// </summary>
    /// <remarks>
    /// Needs <c>ICommitmentOutputMapper</c>, <c>ISweepTransactionBuilder</c> (<c>AddOnchainBitcoinServices</c>),
    /// <c>ILightningSigner</c>, <c>IBitcoinChainService</c> (<c>AddBitcoinInfrastructure</c>), <c>IFeeService</c>, a
    /// scoped <c>IUnitOfWork</c> and <c>IBitcoinWalletService</c>; <c>IChannelMemoryRepository</c> and
    /// <c>IBlockchainMonitor</c> are used when registered.
    /// </remarks>
    public static IServiceCollection AddRemoteCommitResolutionServices(this IServiceCollection services)
    {
        services.AddOptions<RemoteResolutionOptions>();
        services.TryAddSingleton<IRemoteSweepDestination, WalletRemoteSweepDestination>();
        services.TryAddSingleton<IRemoteCommitmentSource, ChainRemoteCommitmentSource>();
        services.TryAddSingleton<RemoteCommitResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutputResolver, RemoteCommitResolver>(
                                      sp => sp.GetRequiredService<RemoteCommitResolver>()));
        return services;
    }
}