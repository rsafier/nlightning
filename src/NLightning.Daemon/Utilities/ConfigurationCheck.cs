using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Utilities;

using Domain.Protocol.ValueObjects;
using Extensions;
using Infrastructure.Bitcoin.Managers;

/// <summary>
/// <c>nltg --check-config</c> (NL-338): binds and validates the configuration the way the node does at start, without
/// a key, bitcoind, the database or the network, and reports what is wrong.
/// </summary>
internal static class ConfigurationCheck
{
    /// <summary>
    /// Builds the node's service graph (<see cref="NodeServiceExtensions.AddNltgNodeServices"/>) over
    /// <paramref name="configuration"/> and runs the options validation the host runs at start
    /// (the startup validator: every section registered with <c>ValidateOnStart</c>).
    /// </summary>
    /// <param name="configuration">The node configuration (from <c>ReadInitialConfiguration</c>).</param>
    /// <param name="network">The resolved network name.</param>
    /// <returns>The validation failures, empty when the configuration is valid.</returns>
    public static IReadOnlyList<string> Run(IConfiguration configuration, string network)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // A throwaway key held in memory only (never saved): registration needs a key manager, validation never uses it
        var unusedKeyPath = Path.Combine(Path.GetTempPath(), $"nltg-check-config-{Guid.NewGuid():N}.key");
        var keyManager = SecureKeyManager.CreateNew(new BitcoinNetwork(network), unusedKeyPath, 0);
        try
        {
            services.AddNltgNodeServices(configuration, keyManager);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or FormatException)
        {
            // A section read while registering (Database) fails here, before any validation runs
            return [e.Message];
        }

        using var provider = services.BuildServiceProvider();
        try
        {
#if NET11_0_OR_GREATER
            // .NET 11 made the startup validation asynchronous (IStartupValidator is obsolete there, SYSLIB0066)
            provider.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
#else
            provider.GetRequiredService<IStartupValidator>().Validate();
#endif
            return [];
        }
        catch (OptionsValidationException e)
        {
            return e.Failures.Distinct().ToList();
        }
        catch (AggregateException e)
        {
            // One section may be validated more than once (ValidateOnStart registered by several layers)
            return e.Flatten().InnerExceptions
                    .SelectMany(inner => inner is OptionsValidationException validation
                                             ? validation.Failures
                                             : [inner.Message])
                    .Distinct()
                    .ToList();
        }
    }
}