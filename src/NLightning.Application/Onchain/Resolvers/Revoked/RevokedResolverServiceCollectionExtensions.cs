using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Onchain.Resolvers.Revoked;

using Domain.Onchain.Interfaces;

public static class RevokedResolverServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RevokedCommitResolver"/> as an <see cref="IOutputResolver"/> (singleton, stateless apart
    /// from a spend cache) and its <see cref="IRevokedCommitDataSource"/>. Needs the on-chain builders
    /// (<c>AddOnchainBitcoinServices</c>), the signer, key derivation, the chain and wallet services, the fee service and
    /// <see cref="Domain.Protocol.Interfaces.ISecretStorageServiceFactory"/>; binds nothing itself (the host may
    /// configure <see cref="RevokedCommitResolverOptions"/>).
    /// </summary>
    public static IServiceCollection AddRevokedCommitResolver(this IServiceCollection services)
    {
        services.TryAddSingleton<IRevokedCommitDataSource, RevokedCommitDataSource>();
        services.AddSingleton<RevokedCommitResolver>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOutputResolver, RevokedCommitResolver>(
                                      sp => sp.GetRequiredService<RevokedCommitResolver>()));
        return services;
    }
}