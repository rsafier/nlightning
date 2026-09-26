using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Onchain.Resolvers.Remote;

public static class RemoteCommitResolutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the peer-commitment resolver (BOLT 5 plan O4): <see cref="IRemoteCommitResolver"/> and its wallet
    /// destination (scoped: they use the scope's unit of work through the wallet service), and the process-wide
    /// <see cref="RemoteResolutionMemory"/>. Needs <c>AddOnchainBitcoinServices()</c> (mapper, sweep builder),
    /// <c>AddBitcoinInfrastructure()</c> (signer, chain broadcaster, outpoint watcher, wallet), the host's
    /// <c>IFeeService</c> and the switch (<c>AddHtlcSwitchServices()</c>). Idempotent.
    /// </summary>
    public static IServiceCollection AddRemoteCommitResolutionServices(this IServiceCollection services)
    {
        services.AddOptions<RemoteResolutionOptions>();
        services.TryAddSingleton<RemoteResolutionMemory>();
        services.TryAddScoped<IRemoteSweepDestination, WalletRemoteSweepDestination>();
        services.TryAddScoped<IRemoteCommitResolver, RemoteCommitResolver>();
        return services;
    }
}