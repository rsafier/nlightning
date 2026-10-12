using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Protocol.Onion.Interfaces;
using Wallet.Interfaces;

public static class OnionReplayPruningServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OnionReplayBlockPruner"/> (NL-327) as a singleton. It needs <see cref="IBlockchainMonitor"/>
    /// and <see cref="IOnionReplayStore"/> (<c>AddInfrastructureServices</c>); the host calls
    /// <see cref="OnionReplayBlockPruner.Start"/> once the chain monitor runs and
    /// <see cref="OnionReplayBlockPruner.StopAsync"/> before stopping it.
    /// </summary>
    public static IServiceCollection AddOnionReplayBlockPruner(this IServiceCollection services)
    {
        services.TryAddSingleton<OnionReplayBlockPruner>();
        return services;
    }
}