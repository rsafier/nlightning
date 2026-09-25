using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Payments.Send;

using Domain.Payments.Interfaces;
using Interfaces;

/// <summary>
/// Registers the send side of payments (ABCD wave 2, lane W2-C).
/// </summary>
public static class PaymentSendServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="PaymentService"/> as a singleton, exposed as <see cref="IPaymentService"/> (which turns on the
    /// <c>payinvoice</c>/<c>listpayments</c> IPC commands) and <see cref="IPaymentOutcomeHandler"/> (the hook the HTLC
    /// switch calls), plus <see cref="PaymentSendOptions"/> with its defaults. Idempotent.
    /// </summary>
    /// <remarks>
    /// Needs <c>AddPaymentsServices()</c> (route and onion builders, <c>TimeProvider</c>), the channel services
    /// (<c>IChannelOperations</c>, <c>IPeerLivenessProbe</c>, <c>IChannelMemoryRepository</c>),
    /// <c>IFailureOnionService</c> and <c>IBlockchainMonitor</c>, and a Scoped <c>IPaymentDbRepository</c> sharing the
    /// scope's <c>IUnitOfWork</c> (<c>AddRepositoriesInfrastructureServices</c>).
    /// </remarks>
    public static IServiceCollection AddPaymentSendServices(this IServiceCollection services)
    {
        services.AddOptions<PaymentSendOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PaymentService>();
        services.TryAddSingleton<IPaymentService>(sp => sp.GetRequiredService<PaymentService>());
        services.TryAddSingleton<IPaymentOutcomeHandler>(sp => sp.GetRequiredService<PaymentService>());

        return services;
    }
}