using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Payments.Send;

using Domain.Gossip.Interfaces;
using Domain.Payments.Interfaces;
using Interfaces;
using Routing;
using Routing.Interfaces;
using Switch;
using Trampoline;

/// <summary>
/// Registers the send side of payments (ABCD wave 2, lane W2-C).
/// </summary>
public static class PaymentSendServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="PaymentService"/> as a singleton, exposed as <see cref="IPaymentService"/> (which turns on the
    /// <c>payinvoice</c>/<c>listpayments</c> IPC commands) and <see cref="IPaymentOutcomeHandler"/>, which the HTLC
    /// switch calls through <see cref="PaymentOutcomeSwitchHandler"/> (an <c>ILocalPaymentHtlcHandler</c>), plus
    /// <see cref="PaymentSendOptions"/> with its defaults and the <see cref="PaymentRoutePlanner"/> (retries and
    /// multi-part payments, NL-270). Idempotent.
    /// </summary>
    /// <remarks>
    /// Needs <c>AddPaymentsServices()</c> (route and onion builders, <c>TimeProvider</c>), the channel services
    /// (<c>IChannelOperations</c>, <c>IPeerLivenessProbe</c>, <c>IChannelMemoryRepository</c>),
    /// <c>IFailureOnionService</c>, <c>ILightningSigner</c> (to verify the <c>channel_update</c> of an UPDATE failure)
    /// and <c>IBlockchainMonitor</c>, and a Scoped <c>IPaymentDbRepository</c> sharing the
    /// scope's <c>IUnitOfWork</c> (<c>AddRepositoriesInfrastructureServices</c>). <c>IAttributionDataService</c>
    /// (<c>AddBitcoinInfrastructure</c>) is optional: with it the origin verifies <c>attribution_data</c> (NL-326).
    /// <para>Graph routing (BOLT 7 plan G4-T2..T4, G3-T5): <see cref="MissionControl"/>, <see cref="GraphPathSource"/>
    /// (it reads the <c>IGraphStore</c> of <c>AddGossipGraphServices</c> when registered; without it payments route
    /// only over direct channels and route hints), <see cref="IRouteQueryService"/> (the payment service, for
    /// <c>getroute</c>) and a no-op <see cref="IGossipScidRefresher"/> (<see cref="NullGossipScidRefresher"/>) that the
    /// host replaces with the gossip sync manager.</para>
    /// <para>Trampoline (NL-875): the payment service is also the <see cref="ITrampolineLegSender"/> the relay engine
    /// sends its outgoing legs through; it reports their ends to the <see cref="ITrampolineLegObserver"/> it resolves
    /// from the container on first use (the engine registers it). The trampoline onion services
    /// (<c>AddBitcoinInfrastructure</c>) and <c>IHopPayloadSerializer</c> are optional: without them no trampoline
    /// onion is built or read.</para>
    /// </remarks>
    public static IServiceCollection AddPaymentSendServices(this IServiceCollection services)
    {
        services.AddOptions<PaymentSendOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PaymentRoutePlanner>();
        services.TryAddSingleton<MissionControl>();
        services.TryAddSingleton<GraphPathSource>();
        services.TryAddSingleton<IGossipScidRefresher, NullGossipScidRefresher>();
        services.TryAddSingleton<PaymentService>();
        services.TryAddSingleton<IPaymentService>(sp => sp.GetRequiredService<PaymentService>());
        services.TryAddSingleton<IRouteQueryService>(sp => sp.GetRequiredService<PaymentService>());
        services.TryAddSingleton<IPaymentOutcomeHandler>(sp => sp.GetRequiredService<PaymentService>());
        services.TryAddSingleton<ITrampolineLegSender>(sp => sp.GetRequiredService<PaymentService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILocalPaymentHtlcHandler, PaymentOutcomeSwitchHandler>());

        return services;
    }
}