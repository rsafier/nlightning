using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Onchain.Fees;

using Domain.Channels.Splicing.Interfaces;

/// <summary>
/// Registers the splice auto-bump (wave SPR, SPR-T3, lane SPR-B).
/// </summary>
public static class SpliceAutoBumperServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="SpliceAutoBumper"/> as itself and <see cref="ISpliceAutoBumper"/> (one singleton). Needs the splice
    /// services (<c>AddSpliceServices</c>), <c>IFeeService</c> and, for the per-block trigger, <c>IBlockchainMonitor</c>;
    /// the options are the <c>Splice</c> section (<c>SpliceOptions.AutoBumpAfterBlocks</c>, off when unset). The host
    /// calls <see cref="SpliceAutoBumper.Start"/> after the chain monitor and the peer manager started and
    /// <see cref="SpliceAutoBumper.StopAsync"/> before it stops the chain monitor. Idempotent (TryAdd).
    /// </summary>
    public static IServiceCollection AddSpliceAutoBumper(this IServiceCollection services)
    {
        services.TryAddSingleton<SpliceAutoBumper>();
        services.TryAddSingleton<ISpliceAutoBumper>(sp => sp.GetRequiredService<SpliceAutoBumper>());
        return services;
    }
}