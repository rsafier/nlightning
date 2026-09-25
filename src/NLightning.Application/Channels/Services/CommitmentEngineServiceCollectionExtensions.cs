using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Services;

using Domain.Channels.Commitments.Interfaces;
using Domain.Protocol.Interfaces;

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

    /// <summary>
    /// Adds what the BOLT 2 normal-operation handlers need besides the engine ports (plan N6-T1): the scoped
    /// <see cref="ChannelStateTransitionService"/> and <see cref="ChannelDomainEventQueue"/>, and an
    /// <see cref="ISecretStorageServiceFactory"/> for the peer's shachain (TryAdd: a host registration wins).
    /// </summary>
    /// <remarks>The transition service also needs <c>IUnitOfWork</c> (Repositories), <c>IMessageSerializer</c>
    /// (Serialization), <c>IMessageFactory</c> and <c>IOptions&lt;NodeOptions&gt;</c>.</remarks>
    public static IServiceCollection AddChannelStateTransitionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISecretStorageServiceFactory, SecretStorageServiceFactory>();
        services.AddScoped<ChannelDomainEventQueue>();
        services.AddScoped<ChannelStateTransitionService>();

        return services;
    }
}