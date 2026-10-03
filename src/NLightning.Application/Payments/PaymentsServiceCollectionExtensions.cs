using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Payments;

using Domain.Payments.Interfaces;
using Events;
using FinalHop;
using Invoices;
using Onion;
using Policy;
using Routing;

/// <summary>
/// Registers the payment core (ABCD wave 1, lane W1-B): onion processing, final hop, forwarding policy, route and
/// onion building for our payments, and invoices.
/// </summary>
public static class PaymentsServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IncomingOnionProcessor"/>, <see cref="FinalHopProcessor"/>, <see cref="IForwardingPolicy"/>
    /// (<see cref="HtlcForwardingPolicy"/>), <see cref="HintRouteBuilder"/>, <see cref="PaymentOnionFactory"/> and
    /// <see cref="IInvoiceService"/> (<see cref="InvoiceService"/>), <see cref="BlindedPathBuilder"/> and the payment event
    /// bus <see cref="PaymentEventHub"/> (<see cref="IPaymentEventPublisher"/>, <see cref="IPaymentEventSource"/>), all
    /// singletons.
    /// </summary>
    /// <remarks>
    /// Needs, from the other layers: <c>ISphinxService</c> (<c>AddBitcoinInfrastructure</c>),
    /// <c>IHopPayloadSerializer</c> (<c>AddSerializationInfrastructureServices</c>), <c>IOnionReplayStore</c>
    /// (<c>AddInfrastructureServices</c>, persistent), <c>ISecureKeyManager</c> and
    /// <c>IOptions&lt;NodeOptions&gt;</c> (host), and a Scoped <c>IInvoiceDbRepository</c> sharing the scope's
    /// database context with <c>IUnitOfWork</c> (<c>AddRepositoriesInfrastructureServices</c>, ABCD W1-C).
    /// </remarks>
    public static IServiceCollection AddPaymentsServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IncomingOnionProcessor>();
        services.AddSingleton<FinalHopProcessor>();
        services.AddSingleton<IForwardingPolicy, HtlcForwardingPolicy>();
        services.AddSingleton<HintRouteBuilder>();
        services.AddSingleton<PaymentOnionFactory>();
        services.AddSingleton<IInvoiceService, InvoiceService>();

        // Cashu plan C0 (NL-901): one bus for invoice and payment outcomes, published by the switch and the payment
        // service after their saves
        services.TryAddSingleton<PaymentEventHub>();
        services.TryAddSingleton<IPaymentEventPublisher>(sp => sp.GetRequiredService<PaymentEventHub>());
        services.TryAddSingleton<IPaymentEventSource>(sp => sp.GetRequiredService<PaymentEventHub>());

        // ONION M5: blinded paths to us (needs IRouteBlindingService, IChannelMemoryRepository and
        // IChannelUpdateService, resolved only when the builder is)
        services.TryAddSingleton(sp => ActivatorUtilities.CreateInstance<BlindedPathBuilder>(sp));

        return services;
    }
}