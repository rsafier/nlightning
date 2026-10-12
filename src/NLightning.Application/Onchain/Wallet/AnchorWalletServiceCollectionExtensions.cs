using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Onchain.Wallet;

using Anchors;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Resolvers.Local;

public static class AnchorWalletServiceCollectionExtensions
{
    /// <summary>
    /// Registers the wallet adapters of BOLT 5 plan O7 (TryAdd singletons, idempotent): <see cref="IAnchorFeeInputSource"/>
    /// (CPFP children, O7-T2) and <see cref="IAnchorFeeInputProvider"/> (anchors HTLC transactions, O7-T3) over the
    /// wallet's <see cref="IFeeInputSelector"/> (O7-T1, registered by <c>AddBitcoinInfrastructure</c>). Without a
    /// selector the CPFP builds no child and the HTLC resolver falls back to
    /// <see cref="UnavailableAnchorFeeInputProvider"/>. Call it before <c>AddLocalCommitResolutionServices</c>, which
    /// only TryAdds that fallback.
    /// </summary>
    public static IServiceCollection AddAnchorWalletServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IAnchorFeeInputSource>(sp =>
            sp.GetService<IFeeInputSelector>() is { } selector
                ? new WalletAnchorFeeInputSource(selector)
                : null!);
        services.TryAddSingleton<IAnchorFeeInputProvider>(sp =>
            sp.GetService<IFeeInputSelector>() is { } selector
                ? new WalletAnchorFeeInputProvider(selector, sp.GetRequiredService<ILightningSigner>(),
                                                   sp.GetRequiredService<ISweepDestinationProvider>())
                : new UnavailableAnchorFeeInputProvider());
        return services;
    }
}