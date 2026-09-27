using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Splicing;

using Domain.Channels.Splicing.Interfaces;
using Interfaces;

/// <summary>
/// Registers splicing (BOLT 2 "Channel Splicing"; splicing plan wave SP1, lane SP1-D).
/// </summary>
public static class SpliceServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="SpliceService"/> as itself, <see cref="ISpliceService"/> and <see cref="ISpliceCommitmentReceiver"/>
    /// (one instance), <see cref="SpliceDepthWatcher"/> (the host resolves it at startup so it subscribes to the chain
    /// monitor), the default <see cref="ISpliceOutDestination"/> (<see cref="WalletSpliceOutDestination"/>) and the
    /// default <see cref="ISpliceStatePort"/> (<see cref="UnavailableSpliceStatePort"/>, until lanes SP1-B/SP1-C
    /// register theirs first). Idempotent (TryAdd). The message handlers (<c>splice_init</c>, <c>splice_ack</c>,
    /// <c>splice_locked</c>) are registered by <c>AddApplicationServices</c>' reflection scan; the options are the
    /// <c>Splice</c> section (<see cref="SpliceOptions"/>). Needs quiescence (<c>AddQuiescenceServices</c>) and the
    /// interactive-tx driver (<c>AddInteractiveTxServices</c>).
    /// </summary>
    public static IServiceCollection AddSpliceServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISpliceStatePort, UnavailableSpliceStatePort>();
        services.TryAddSingleton<ISpliceOutDestination, WalletSpliceOutDestination>();
        services.TryAddSingleton<SpliceService>();
        services.TryAddSingleton<ISpliceService>(sp => sp.GetRequiredService<SpliceService>());
        services.TryAddSingleton<ISpliceCommitmentReceiver>(sp => sp.GetRequiredService<SpliceService>());
        services.TryAddSingleton<SpliceDepthWatcher>();
        return services;
    }
}