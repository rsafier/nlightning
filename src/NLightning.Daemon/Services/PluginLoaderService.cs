using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Services;

using Models;
using Plugins;

public class PluginLoaderService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<PluginLoaderService> _logger;
    private readonly List<(IDaemonPlugin Plugin, AssemblyLoadContext Alc)> _plugins = new();

    public PluginLoaderService(IServiceProvider services, IConfiguration config, ILogger<PluginLoaderService> logger)
    {
        _services = services;
        _config = config;
        _logger = logger;
    }

    // Loading assemblies at run time needs dynamic code and unreferenced metadata: a NativeAOT build skips the
    // plugins (the guard below), and trimmed JIT builds are not a supported target (NL-338)
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "NativeAOT skips plugins; no trimmed JIT build")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "NativeAOT skips plugins; no trimmed JIT build")]
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var entries = _config.GetSection("Plugins").Get<List<PluginEntry>>() ?? [];
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            if (entries.Count > 0)
                _logger.LogWarning("Plugins cannot be loaded in a NativeAOT build; {Count} configured plugin(s) skipped",
                                   entries.Count);

            return;
        }

        foreach (var entry in entries)
        {
            try
            {
                var alc = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(entry.AssemblyPath),
                                                  isCollectible: true);
                await using var stream = File.OpenRead(entry.AssemblyPath);
                var asm = alc.LoadFromStream(stream);

                var pluginType = string.IsNullOrWhiteSpace(entry.TypeName)
                                     ? asm.ExportedTypes.First(t => typeof(IDaemonPlugin).IsAssignableFrom(t) &&
                                                                    !t.IsAbstract)
                                     : asm.GetType(entry.TypeName!, throwOnError: true)!;

                var plugin = (IDaemonPlugin)ActivatorUtilities.CreateInstance(
                    _services, pluginType);

                var context = _services.GetRequiredService<IDaemonContext>();
                await plugin.StartAsync(context, cancellationToken);

                _plugins.Add((plugin, alc));
                _logger.LogInformation("Loaded plugin {Plugin}", pluginType.FullName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load plugin from {Path}", entry.AssemblyPath);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (plugin, alc) in _plugins)
        {
            try { await plugin.StopAsync(cancellationToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping plugin {Name}", plugin.Name); }

            await plugin.DisposeAsync();
            alc.Unload();
        }

        _plugins.Clear();
    }
}