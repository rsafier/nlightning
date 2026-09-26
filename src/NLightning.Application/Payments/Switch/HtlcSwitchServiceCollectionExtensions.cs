using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Payments.Switch;

using Channels.Interfaces;
using Channels.Reestablish;
using Domain.Channels.Interfaces;

/// <summary>
/// Registers the forwarding and receiving HTLC switch (ABCD W2-B).
/// </summary>
public static class HtlcSwitchServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the registered <see cref="IHtlcSwitch"/> (by default <c>LocalOnlyHtlcSwitch</c>, which fails every
    /// HTLC back) with <see cref="HtlcSwitch"/>, a singleton, and decorates the registered
    /// <see cref="IPeerLivenessProbe"/> with <see cref="LinkUpReplayingPeerLivenessProbe"/>, so every
    /// <c>MarkLinkUp</c> replays the channel's pending HTLC events (<see cref="LinkUpEventReplayer"/>), except right after
    /// a channel_reestablish, whose events <c>ChannelManager</c> replays itself (NL-264; needs the registered
    /// <see cref="ReestablishTracker"/>).
    /// </summary>
    /// <remarks>
    /// Call it after <c>AddChannelOperationsServices()</c> and <c>AddPaymentsServices()</c> (both inside
    /// <c>AddApplicationServices</c>), and after any other <see cref="IPeerLivenessProbe"/> registration. Besides those it
    /// needs <c>IFailureOnionService</c> (<c>AddBitcoinInfrastructure</c>) and a scoped <c>IUnitOfWork</c> with the
    /// forward-circuit and invoice repositories (<c>AddRepositoriesInfrastructureServices</c>); <c>IBlockchainMonitor</c>,
    /// <c>IChannelUpdateService</c> (UPDATE failures carry our signed <c>channel_update</c>) and
    /// <see cref="ILocalPaymentHtlcHandler"/>s are used when registered. Calling it twice changes nothing more.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No <see cref="IPeerLivenessProbe"/> is registered yet.</exception>
    public static IServiceCollection AddHtlcSwitchServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // The switch is the container's own singleton, so the container disposes it (its mpp_timeout timers) also when
        // a decorator (DustExposureHtlcSwitch) wraps the IHtlcSwitch registration
        services.TryAddSingleton<HtlcSwitch>();
        services.Replace(ServiceDescriptor.Singleton<IHtlcSwitch>(sp => sp.GetRequiredService<HtlcSwitch>()));

        if (services.Any(d => d.ServiceType == typeof(LinkUpEventReplayer)))
            return services;

        var probe = services.LastOrDefault(d => d.ServiceType == typeof(IPeerLivenessProbe))
                 ?? throw new InvalidOperationException(
                        "Register IPeerLivenessProbe (AddChannelOperationsServices) before AddHtlcSwitchServices");
        services.AddSingleton<LinkUpEventReplayer>();
        services.Replace(ServiceDescriptor.Singleton<IPeerLivenessProbe>(
                             sp => new LinkUpReplayingPeerLivenessProbe(CreateInner(sp, probe),
                                                                        sp.GetRequiredService<LinkUpEventReplayer>(),
                                                                        sp.GetService<ReestablishTracker>())));
        return services;
    }

    private static IPeerLivenessProbe CreateInner(IServiceProvider serviceProvider, ServiceDescriptor descriptor) =>
        (IPeerLivenessProbe)(descriptor.ImplementationInstance
                          ?? descriptor.ImplementationFactory?.Invoke(serviceProvider)
                          ?? ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType!));
}