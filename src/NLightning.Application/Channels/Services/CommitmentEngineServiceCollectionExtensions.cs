using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Services;

using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Protocol.Interfaces;
using Interfaces;
using Switch;

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

    /// <summary>
    /// Adds the send side of the normal operation (plan N6-T2): <see cref="IChannelOperations"/>
    /// (<see cref="ChannelOperationsService"/>), <see cref="ICommitScheduler"/> (<see cref="CommitScheduler"/>, options
    /// <see cref="CommitSchedulerOptions"/>), the default <see cref="IPeerLivenessProbe"/> and the default
    /// <see cref="IHtlcSwitch"/> (<see cref="LocalOnlyHtlcSwitch"/>), all singletons.
    /// </summary>
    /// <remarks>
    /// The probe and the switch are <c>TryAdd</c>ed: a host registration made before wins, and a later one replaces
    /// them with <c>services.Replace(...)</c> (the forwarding switch of ABCD W2-B). Needs an
    /// <see cref="IChannelMessagePublisher"/> (<c>ChannelManager</c>, registered by <c>AddApplicationServices</c>),
    /// <c>ISphinxService</c> and <c>IFailureOnionService</c> (Infrastructure.Bitcoin; the latter needs Serialization's
    /// <c>IFailureMessageSerializer</c>), and <c>IOptions&lt;NodeOptions&gt;</c>.
    /// </remarks>
    public static IServiceCollection AddChannelOperationsServices(this IServiceCollection services)
    {
        services.AddOptions<CommitSchedulerOptions>();
        services.TryAddSingleton<IPeerLivenessProbe, ConnectedPeerLivenessProbe>();
        services.AddSingleton<ICommitScheduler, CommitScheduler>();
        services.AddSingleton<IChannelOperations, ChannelOperationsService>();
        services.TryAddSingleton<IHtlcSwitch, LocalOnlyHtlcSwitch>();

        return services;
    }
}