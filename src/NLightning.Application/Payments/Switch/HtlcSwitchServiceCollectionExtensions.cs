using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Payments.Switch;

using Domain.Channels.Interfaces;

/// <summary>
/// Registers the forwarding and receiving HTLC switch (ABCD W2-B).
/// </summary>
public static class HtlcSwitchServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the registered <see cref="IHtlcSwitch"/> (by default <c>LocalOnlyHtlcSwitch</c>, which fails every
    /// HTLC back) with <see cref="HtlcSwitch"/>, a singleton.
    /// </summary>
    /// <remarks>
    /// Call it after <c>AddChannelOperationsServices()</c> and <c>AddPaymentsServices()</c> (both inside
    /// <c>AddApplicationServices</c>). Besides those it needs <c>IFailureOnionService</c> (<c>AddBitcoinInfrastructure</c>)
    /// and a scoped <c>IUnitOfWork</c> with the forward-circuit and invoice repositories
    /// (<c>AddRepositoriesInfrastructureServices</c>); <c>IBlockchainMonitor</c>, <c>IChannelUpdateService</c> (UPDATE
    /// failures carry our signed <c>channel_update</c>) and <see cref="ILocalPaymentHtlcHandler"/>s are used when
    /// registered.
    /// </remarks>
    public static IServiceCollection AddHtlcSwitchServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.Replace(ServiceDescriptor.Singleton<IHtlcSwitch, HtlcSwitch>());
        return services;
    }
}