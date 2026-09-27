using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Channels.DualFunding;

using Domain.Channels.DualFunding.Interfaces;

/// <summary>
/// Registers the dual-funded open (BOLT 2 "Channel Establishment v2", splicing plan wave DF).
/// </summary>
public static class DualFundingServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="DualFundedOpenService"/> (singleton, as itself and as <see cref="IDualFundedOpenService"/>) and
    /// <see cref="DualFundReestablish"/> (scoped). It needs the interactive-tx driver (<c>AddInteractiveTxServices</c>),
    /// the Domain channel services of <c>AddApplicationServices</c> and <c>AddBitcoinInfrastructure</c>. The host binds
    /// <see cref="DualFundingOptions"/> from <see cref="DualFundingOptions.SectionName"/>. The
    /// <c>open_channel2</c>/<c>accept_channel2</c> handlers are registered by the reflection scan.
    /// </summary>
    public static IServiceCollection AddDualFundingServices(this IServiceCollection services)
    {
        services.TryAddSingleton<DualFundedOpenService>();
        services.TryAddSingleton<IDualFundedOpenService>(sp => sp.GetRequiredService<DualFundedOpenService>());
        services.TryAddScoped<DualFundReestablish>();
        return services;
    }
}