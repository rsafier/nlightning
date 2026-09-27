using Microsoft.Extensions.Hosting;

namespace NLightning.Daemon.Services;

using Application.Channels.Backup;

/// <summary>
/// Starts and stops the <see cref="ChannelBackupMonitor"/>, which keeps <c>channel.backup</c> up to date.
/// </summary>
public sealed class ChannelBackupHostedService : IHostedService
{
    private readonly ChannelBackupMonitor _monitor;

    public ChannelBackupHostedService(ChannelBackupMonitor monitor)
    {
        _monitor = monitor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitor.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _monitor.StopAsync();
}