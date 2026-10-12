using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure.Bitcoin.Onchain;

using Builders;
using Builders.Interfaces;
using Interfaces;

public static class OnchainServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BOLT 5 on-chain building blocks of this layer: <see cref="ICommitmentOutputMapper"/> (needs the
    /// Domain <c>ICommitmentTransactionModelFactory</c> from <c>AddApplicationServices</c> and the commitment builder
    /// from <c>AddBitcoinInfrastructure</c>), <see cref="ISweepTransactionBuilder"/> (needs <c>IOptions&lt;NodeOptions&gt;</c>)
    /// and <see cref="IPenaltyTransactionBuilder"/>.
    /// </summary>
    public static IServiceCollection AddOnchainBitcoinServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ICommitmentOutputMapper, CommitmentOutputMapper>();
        services.TryAddSingleton<ISweepTransactionBuilder, SweepTransactionBuilder>();
        services.TryAddSingleton<IPenaltyTransactionBuilder, PenaltyTransactionBuilder>();
        return services;
    }
}