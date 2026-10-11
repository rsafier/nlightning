using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.InteractiveTx;

using Interfaces;

/// <summary>
/// Registers the interactive-tx driver (splicing plan wave IT, IT4-T1).
/// </summary>
public static class InteractiveTxServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IInteractiveTxDriver"/> (one singleton for every channel) over
    /// <see cref="DomainInteractiveTxEngine"/>. The driver needs <c>IInteractiveTxBuilder</c>, <c>IPrevTxInspector</c>
    /// (Infrastructure.Bitcoin, lane IT-B) and <c>IInteractiveTxContributor</c> (<c>WalletInteractiveTxContributor</c>),
    /// all singletons, and uses <c>IQuiescenceService</c> when one is registered. The driver is built by a factory so
    /// a <c>ValidateOnBuild</c> host still builds without them; the <c>Tx*MessageHandler</c>s answer
    /// <c>tx_abort</c> when no driver is registered.
    /// </summary>
    public static IServiceCollection AddInteractiveTxServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IInteractiveTxEngine, DomainInteractiveTxEngine>();
        services.TryAddSingleton<InteractiveTxDriver>(sp => ActivatorUtilities.CreateInstance<InteractiveTxDriver>(sp));
        services.TryAddSingleton<IInteractiveTxDriver>(sp => sp.GetRequiredService<InteractiveTxDriver>());
        return services;
    }
}