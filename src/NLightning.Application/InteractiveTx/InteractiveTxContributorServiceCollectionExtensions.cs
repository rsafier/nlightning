using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.InteractiveTx;

using Domain.Protocol.InteractiveTx.Interfaces;

/// <summary>
/// Registers <see cref="WalletInteractiveTxContributor"/> (splicing plan IT2-T3).
/// </summary>
public static class InteractiveTxContributorServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="WalletInteractiveTxContributor"/> as a singleton, as itself and as
    /// <see cref="IInteractiveTxContributor"/> (TryAdd, idempotent). It must be a singleton: its release/sign gate and
    /// the in-process part of the IT-ABT-01 guard live on the instance. Needs the wallet (<c>IFeeInputSelector</c>,
    /// <c>ILightningSigner</c>, <c>IUtxoMemoryRepository</c>) and Infrastructure.Bitcoin's
    /// <c>AddInteractiveTxBitcoinServices()</c>; the scope factory gives it the stored negotiations
    /// (<c>IUnitOfWork.InteractiveTxSessionDbRepository</c>). The host calls
    /// <see cref="WalletInteractiveTxContributor.ReleaseOrphanedReservationsAsync"/> once at startup, after the wallet
    /// is loaded and before negotiations start.
    /// </summary>
    public static IServiceCollection AddInteractiveTxContributorServices(this IServiceCollection services)
    {
        services.TryAddSingleton<WalletInteractiveTxContributor>();
        services.TryAddSingleton<IInteractiveTxContributor>(sp =>
                                                                sp.GetRequiredService<WalletInteractiveTxContributor>());

        return services;
    }
}