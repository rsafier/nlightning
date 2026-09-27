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
    /// (one instance), <see cref="SpliceDepthWatcher"/> (the host resolves it at startup and awaits
    /// <see cref="SpliceDepthWatcher.CatchUpAsync"/> after the chain monitor and the peer manager started), the default <see cref="ISpliceOutDestination"/> (<see cref="WalletSpliceOutDestination"/>) and the
    /// default <see cref="ISpliceStatePort"/> (<see cref="EngineSpliceStatePort"/>, over the several-funding engine, the
    /// per-funding signer and the <c>ChannelFundings</c> rows). Idempotent (TryAdd). The message handlers (<c>splice_init</c>, <c>splice_ack</c>,
    /// <c>splice_locked</c>) are registered by <c>AddApplicationServices</c>' reflection scan; the options are the
    /// <c>Splice</c> section (<see cref="SpliceOptions"/>). Needs quiescence (<c>AddQuiescenceServices</c>) and the
    /// interactive-tx driver (<c>AddInteractiveTxServices</c>).
    /// </summary>
    public static IServiceCollection AddSpliceServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ISpliceStatePort, EngineSpliceStatePort>();
        services.TryAddSingleton<ISpliceOutDestination, WalletSpliceOutDestination>();
        services.TryAddSingleton<SpliceService>();
        services.TryAddSingleton<ISpliceService>(sp => sp.GetRequiredService<SpliceService>());
        services.TryAddSingleton<ISpliceCommitmentReceiver>(sp => sp.GetRequiredService<SpliceService>());
        services.TryAddSingleton<SpliceDepthWatcher>();
        return services;
    }
}