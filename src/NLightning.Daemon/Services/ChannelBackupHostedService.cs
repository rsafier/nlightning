using Microsoft.Extensions.Hosting;

namespace NLightning.Daemon.Services;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;

/// <summary>
/// Starts and stops the <see cref="ChannelBackupMonitor"/>, which keeps <c>channel.backup</c> up to date, and resumes
/// the funding spend searches of restored recovery channels (NL-430; they live in memory only).
/// </summary>
public sealed class ChannelBackupHostedService : IHostedService
{
    private readonly ChannelBackupMonitor _monitor;
    private readonly IChannelRestoreService? _restoreService;

    public ChannelBackupHostedService(ChannelBackupMonitor monitor, IChannelRestoreService? restoreService = null)
    {
        _monitor = monitor;
        _restoreService = restoreService;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitor.Start();

        // Background: each channel is looked up once the start-up registration has loaded it
        _restoreService?.ResumeSpendSearches();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _monitor.StopAsync();
}