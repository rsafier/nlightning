using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure.Bitcoin.Onion.Trampoline;

using Domain.Protocol.Onion.Interfaces;

public static class TrampolineOnionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the BOLT 4 trampoline onion crypto (BOLTs PR 836) as stateless singletons:
    /// <see cref="ITrampolineOnionService"/> (needs <see cref="ISphinxService"/>, and the host's
    /// <c>ISecureKeyManager</c> to peel as the local node).
    /// </summary>
    public static IServiceCollection AddTrampolineOnionServices(this IServiceCollection services)
    {
        services.TryAddSingleton<ITrampolineOnionService, TrampolineOnionService>();
        return services;
    }
}