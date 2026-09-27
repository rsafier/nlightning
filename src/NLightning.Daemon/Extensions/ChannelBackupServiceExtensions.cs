using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Extensions;

using Application.Channels.Backup;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Handlers;
using Interfaces;
using Services;

/// <summary>
/// Static channel backups (SCB) in the node: the Application service and monitor, <c>Node:Backup</c>, the client and
/// IPC handlers of <c>exportchanbackup</c> (ClientCommand 21) and <c>verifychanbackup</c> (22).
/// </summary>
public static class ChannelBackupServiceExtensions
{
    /// <summary>
    /// Registers the backup services, binds <see cref="ChannelBackupOptions"/> from <c>Node:Backup</c> and registers
    /// the client and IPC handlers. Call it from <c>AddNltgNodeServices</c> (the Docker test node gets it too, but
    /// without a file unless its configuration sets <c>Node:Backup:FilePath</c>).
    /// </summary>
    public static IServiceCollection AddChannelBackupNodeServices(this IServiceCollection services,
                                                                  IConfiguration configuration)
    {
        services.AddChannelBackupServices();
        services.Configure<ChannelBackupOptions>(configuration.GetSection(ChannelBackupOptions.SectionName));

        services.AddScoped<IClientCommandHandler<ExportChanBackupClientRequest, ExportChanBackupClientResponse>,
            ExportChanBackupClientHandler>();
        services.AddScoped<IClientCommandHandler<VerifyChanBackupClientRequest, VerifyChanBackupClientResponse>,
            VerifyChanBackupClientHandler>();
        services.AddSingleton<IIpcCommandHandler, ExportChanBackupIpcHandler>();
        services.AddSingleton<IIpcCommandHandler, VerifyChanBackupIpcHandler>();
        return services;
    }

    /// <summary>
    /// The daemon's part: <c>channel.backup</c> in the configuration directory unless <c>Node:Backup:FilePath</c> is
    /// set, and the hosted service that keeps it up to date. Call it from <c>ConfigureNltgServices</c>.
    /// </summary>
    public static IServiceCollection AddChannelBackupFile(this IServiceCollection services, string configPath)
    {
        services.PostConfigure<ChannelBackupOptions>(options =>
        {
            if (string.IsNullOrWhiteSpace(options.FilePath))
                options.FilePath = Path.Combine(configPath, ChannelBackupOptions.DefaultFileName);
        });
        services.AddHostedService<ChannelBackupHostedService>();
        return services;
    }
}