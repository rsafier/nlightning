using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure.Bitcoin.Onchain;

using Interfaces;

public static class OnchainServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BOLT 5 on-chain building blocks of this layer: <see cref="ICommitmentOutputMapper"/> (needs the
    /// Domain <c>ICommitmentTransactionModelFactory</c> from <c>AddApplicationServices</c> and the commitment builder
    /// from <c>AddBitcoinInfrastructure</c>).
    /// </summary>
    public static IServiceCollection AddOnchainBitcoinServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ICommitmentOutputMapper, CommitmentOutputMapper>();
        return services;
    }
}