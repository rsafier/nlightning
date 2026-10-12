using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Payments.Trampoline;

using Switch;

/// <summary>
/// Registers the trampoline relay engine (NL-875 TR3).
/// </summary>
public static class TrampolineRelayServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="TrampolineRelayService"/> as a singleton, exposed as the switch's
    /// <see cref="ITrampolineRelayIngress"/> and <see cref="ITrampolineHtlcHandler"/> and as the
    /// <see cref="ITrampolineLegObserver"/> the leg sender reports to, plus <see cref="TrampolineOptions"/> with its
    /// defaults (the daemon binds <c>Node:Trampoline</c>). Idempotent.
    /// </summary>
    /// <remarks>
    /// The engine resolves the <see cref="ITrampolineLegSender"/> (the payment service) when it needs it, so the two
    /// can depend on each other; without one every relay part is failed with <c>temporary_trampoline_failure</c>. It
    /// needs the services of <c>AddPaymentsServices()</c> and <c>AddHtlcSwitchServices()</c> (call it after them) and
    /// a scoped <c>IUnitOfWork</c> that stores trampoline relays. The host calls
    /// <see cref="TrampolineRelayService.StartAsync"/> once the channels are loaded and the payments reconciled.
    /// </remarks>
    public static IServiceCollection AddTrampolineRelayServices(this IServiceCollection services)
    {
        services.AddOptions<TrampolineOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<TrampolineRelayService>();
        services.TryAddSingleton<ITrampolineRelayIngress>(sp => sp.GetRequiredService<TrampolineRelayService>());
        services.TryAddSingleton<ITrampolineHtlcHandler>(sp => sp.GetRequiredService<TrampolineRelayService>());
        services.TryAddSingleton<ITrampolineLegObserver>(sp => sp.GetRequiredService<TrampolineRelayService>());
        return services;
    }
}