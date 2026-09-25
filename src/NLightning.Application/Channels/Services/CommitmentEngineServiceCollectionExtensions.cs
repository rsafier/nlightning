using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Channels.Services;

using Domain.Channels.Commitments.Interfaces;

/// <summary>
/// Registers the commitment signing service and the commitment state machine's crypto ports (NL-230).
/// </summary>
public static class CommitmentEngineServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="CommitmentSigningService"/> and the engine ports <see cref="ICommitmentSigner"/>,
    /// <see cref="ICommitmentVerifier"/> and <see cref="IRevocationVerifier"/>, all singletons.
    /// </summary>
    /// <remarks>Needs <c>IChannelMemoryRepository</c> (Repositories), <c>ILightningSigner</c> and
    /// <c>IPerCommitmentSecretVerifier</c> (Infrastructure.Bitcoin), the commitment/HTLC builders and
    /// <c>ICommitmentTransactionModelFactory</c> (Application).</remarks>
    public static IServiceCollection AddCommitmentEngineServices(this IServiceCollection services)
    {
        services.AddSingleton<CommitmentSigningService>();
        services.AddSingleton<ICommitmentSigner, EngineCommitmentSignerPort>();
        services.AddSingleton<ICommitmentVerifier, EngineCommitmentVerifierPort>();
        services.AddSingleton<IRevocationVerifier, EngineRevocationVerifierPort>();

        return services;
    }
}