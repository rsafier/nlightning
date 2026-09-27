using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Interfaces;

public static class ChannelBackupServiceCollectionExtensions
{
    /// <summary>
    /// Registers the static channel backup service and its file monitor (singletons, idempotent). The host binds
    /// <see cref="ChannelBackupOptions"/> (<c>Node:Backup</c>) and starts/stops the <see cref="ChannelBackupMonitor"/>.
    /// Needs the node's <see cref="ISecureKeyManager"/>, <see cref="ILightningSigner"/>, the scoped
    /// <c>IUnitOfWork</c> and <see cref="IChannelMemoryRepository"/>.
    /// </summary>
    public static IServiceCollection AddChannelBackupServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IChannelBackupService>(sp => new ChannelBackupService(
                                                            sp.GetRequiredService<IServiceScopeFactory>(),
                                                            sp.GetRequiredService<ISecureKeyManager>(),
                                                            sp.GetRequiredService<ILightningSigner>(),
                                                            sp.GetRequiredService<IOptions<NodeOptions>>(),
                                                            sp.GetService<IOptions<ChannelBackupOptions>>(),
                                                            sp.GetService<TimeProvider>(),
                                                            sp.GetService<ILogger<ChannelBackupService>>()));
        services.TryAddSingleton(sp => new ChannelBackupMonitor(sp.GetRequiredService<IChannelBackupService>(),
                                                                sp.GetRequiredService<IChannelMemoryRepository>(),
                                                                sp.GetService<IOptions<ChannelBackupOptions>>(),
                                                                sp.GetService<TimeProvider>(),
                                                                sp.GetService<ILogger<ChannelBackupMonitor>>()));
        return services;
    }
}