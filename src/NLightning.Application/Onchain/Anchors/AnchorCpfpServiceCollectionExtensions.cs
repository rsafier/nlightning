using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Onchain.Anchors;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Onchain.Fees;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Resolvers.Local;

public static class AnchorCpfpServiceCollectionExtensions
{
    /// <summary>
    /// Registers the anchor CPFP (BOLT 5 plan O7-T2): <see cref="IAnchorChildTransactionBuilder"/>,
    /// <see cref="AnchorCpfpPolicy"/> and <see cref="AnchorCpfpService"/> as itself and <see cref="IAnchorCpfpService"/>
    /// (TryAdd singletons, idempotent), and <see cref="AnchorCpfpOptions"/>.
    /// </summary>
    /// <remarks>
    /// Call it after <c>AddOnchainServices</c> (the shared <see cref="SweepFeePolicy"/>) and
    /// <c>AddLocalCommitResolutionServices</c> (the <see cref="ISweepDestinationProvider"/> for change and anchor
    /// sweeps). <see cref="IAnchorFeeInputSource"/> (the wallet's fee inputs, O7-T1) is optional: without it no child is
    /// built and a warning is logged at start; it must keep its reservations durably. <see cref="IBitcoinChainService"/>
    /// is optional too: with it the reservation of a confirmed commitment's pending child goes back as soon as the
    /// anchor is spent on chain and the sweep skips spent anchors. Nothing for the host to start:
    /// <c>ChannelFailureService.Start</c>/<c>Stop</c> start and stop it, and its publish path calls
    /// <see cref="IAnchorCpfpService.ScheduleCommitmentRound"/>. The host binds <see cref="AnchorCpfpOptions"/> from
    /// <see cref="AnchorCpfpOptions.SectionName"/>.
    /// </remarks>
    public static IServiceCollection AddAnchorCpfpServices(this IServiceCollection services)
    {
        services.AddOptions<AnchorCpfpOptions>();
        services.TryAddSingleton<IAnchorChildTransactionBuilder, AnchorChildTransactionBuilder>();
        services.TryAddSingleton(sp => new AnchorCpfpPolicy(sp.GetRequiredService<SweepFeePolicy>(),
                                                            sp.GetService<IOptions<AnchorCpfpOptions>>()?.Value));
        services.TryAddSingleton(sp => new AnchorCpfpService(sp.GetRequiredService<IBlockchainMonitor>(),
                                                             sp.GetRequiredService<IAnchorChildTransactionBuilder>(),
                                                             sp.GetRequiredService<IChannelLockProvider>(),
                                                             sp.GetRequiredService<IChannelMemoryRepository>(),
                                                             sp.GetRequiredService<IFeeService>(),
                                                             sp.GetRequiredService<ILightningSigner>(),
                                                             sp.GetRequiredService<ILogger<AnchorCpfpService>>(),
                                                             sp.GetRequiredService<AnchorCpfpPolicy>(),
                                                             sp.GetRequiredService<IServiceScopeFactory>(),
                                                             sp.GetRequiredService<ISweepDestinationProvider>(),
                                                             sp.GetService<IAnchorFeeInputSource>(),
                                                             sp.GetService<IOptions<AnchorCpfpOptions>>()?.Value,
                                                             sp.GetService<IBitcoinChainService>()));
        services.TryAddSingleton<IAnchorCpfpService>(sp => sp.GetRequiredService<AnchorCpfpService>());
        return services;
    }
}