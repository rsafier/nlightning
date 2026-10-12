using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.Fees;

using Domain.Channels.Interfaces;

/// <summary>
/// Registers the fee and dust exposure services of BOLT2 plan N9-T1/N9-T3.
/// </summary>
public static class ChannelFeeServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IFeeUpdateScheduler"/> (<see cref="FeeUpdateScheduler"/>, a singleton; the host starts it with
    /// <see cref="IFeeUpdateScheduler.StartAsync"/> after the channels are loaded and stops it on shutdown) and
    /// decorates the registered <see cref="IHtlcSwitch"/> with <see cref="DustExposureHtlcSwitch"/> (the receiver rule of
    /// <c>max_dust_htlc_exposure_msat</c>).
    /// </summary>
    /// <remarks>
    /// Call it last in <c>AddApplicationServices</c>: after <c>AddHtlcSwitchServices()</c> (which replaces the switch)
    /// and <c>AddPaymentsServices()</c> (<c>IncomingOnionProcessor</c>). It also needs <c>IFeeService</c> (host),
    /// <c>IFailureOnionService</c> (<c>AddBitcoinInfrastructure</c>) and a scoped <c>IUnitOfWork</c>. Calling it twice
    /// changes nothing more.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No <see cref="IHtlcSwitch"/> is registered yet.</exception>
    public static IServiceCollection AddChannelFeeServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IFeeUpdateScheduler, FeeUpdateScheduler>();

        if (services.Any(d => d.ServiceType == typeof(DustExposureSwitchMarker)))
            return services;

        var current = services.LastOrDefault(d => d.ServiceType == typeof(IHtlcSwitch))
                   ?? throw new InvalidOperationException(
                          "Register IHtlcSwitch (AddChannelOperationsServices / AddHtlcSwitchServices) before "
                        + "AddChannelFeeServices");
        services.AddSingleton<DustExposureSwitchMarker>();
        services.Replace(ServiceDescriptor.Singleton<IHtlcSwitch>(
                             sp => ActivatorUtilities.CreateInstance<DustExposureHtlcSwitch>(
                                 sp, CreateInner(sp, current))));
        return services;
    }

    /// <summary>Marks the switch as decorated, so a second call does not wrap it twice.</summary>
    private sealed class DustExposureSwitchMarker;

    private static IHtlcSwitch CreateInner(IServiceProvider serviceProvider, ServiceDescriptor descriptor) =>
        (IHtlcSwitch)(descriptor.ImplementationInstance
                   ?? descriptor.ImplementationFactory?.Invoke(serviceProvider)
                   ?? ActivatorUtilities.CreateInstance(serviceProvider, descriptor.ImplementationType!));
}