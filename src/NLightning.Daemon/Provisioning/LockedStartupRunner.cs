using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace NLightning.Daemon.Provisioning;

using Configuration;
using Infrastructure.Bitcoin.Managers;
using Utilities;

/// <summary>
/// The host side of a locked start (NL-1349): refuses what a locked start cannot do, daemonizes first when asked, opens
/// the provisioning endpoint and waits (SIGINT/SIGTERM end the wait) for <see cref="LockedStartup"/> to accept a key.
/// </summary>
internal static class LockedStartupRunner
{
    /// <summary>How long a socket connection may take to send one request.</summary>
    private static readonly TimeSpan s_socketRequestTimeout = TimeSpan.FromSeconds(60);

    /// <returns>An exit code to return, or the accepted key and configuration.</returns>
    public static async Task<(int? ExitCode, UnlockedNode? Node)> RunAsync(
        string[] args, IConfiguration initialConfig, string network, string configPath, string pidFilePath,
        StartupOptions startupOptions, SigningOptions signingOptions)
    {
        var errors = new List<string>(startupOptions.GetValidationErrors(configPath));
        if (signingOptions.IsRemote)
            errors.Add("A locked start needs Signing:Mode=Local: a remote signer holds the key itself.");
        // The daemon child's --password-stdin is the launcher's own (an empty line, never read)
        if ((!DaemonUtils.IsRunningAsDaemon() && args.Any(arg => PasswordUtils.GetPasswordOptionLength(arg) > 0))
         || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable)))
            errors.Add("A locked start takes its key and password only from the key provisioner; remove the password "
                     + "options and NLTG_PASSWORD.");
        if (startupOptions.IsStdin && (DaemonUtils.GetDaemonArgument(args) ?? initialConfig.GetValue<bool>("Node:Daemon"))
                                   && !DaemonUtils.IsRunningAsDaemon())
            errors.Add("Node:Startup:Provisioner=Stdin cannot run as a daemon (the daemon has no stdin).");

        Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, null);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
                Log.Error("{Error}", error);
            return (1, null);
        }

        // Daemonize before the wait: the child opens the endpoint. The child gets an empty stdin line, never a password
        if (DaemonUtils.StartDaemonIfRequested(args, initialConfig, pidFilePath, Log.Logger, string.Empty))
            return (0, null);

        if (File.Exists(SecureKeyManager.GetKeyFilePath(configPath)))
            Log.Warning("A locked start ignores the key file in the configuration directory; the key comes from the "
                      + "provisioner");

        if (startupOptions.IsStdin)
        {
            // stdout carries the provisioning answers: the log goes to stderr until the node starts
            Log.Logger = new LoggerConfiguration().WriteTo.Console(standardErrorFromLevel: LogEventLevel.Verbose)
                                                  .CreateBootstrapLogger();
        }

        using var stop = new CancellationTokenSource();
        var registrations = new List<PosixSignalRegistration>();
        if (!OperatingSystem.IsWindows())
        {
            foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM })
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    // ReSharper disable once AccessToDisposedClosure
                    stop.Cancel();
                }));
            }
        }

        using var loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: false);
        var logger = loggerFactory.CreateLogger("NLightning.Daemon.LockedStartup");
        LockedStartup? startup = null;
        IProvisioningEndpoint endpoint = startupOptions.IsStdin
                                             ? StdioProvisioningEndpoint.FromConsole()
                                             : new UnixSocketProvisioningEndpoint(
                                                 startupOptions.GetSocketPath(configPath));
        var provisioner = new EndpointKeyProvisioner(endpoint, () => startup!.Status(), logger,
                                                     startupOptions.IsStdin ? null : s_socketRequestTimeout);
        try
        {
            startup = new LockedStartup(network, configPath, initialConfig, provisioner, logger,
                                        TimeSpan.FromMilliseconds(startupOptions.FailureDelayMilliseconds));
            var unlocked = await startup.RunAsync(stop.Token);
            return unlocked is null ? (1, null) : (null, unlocked);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            Log.Information("Stopped while locked");
            return (0, null);
        }
        finally
        {
            // The endpoint closes (and the socket file goes) before the node starts
            await provisioner.DisposeAsync();
            foreach (var registration in registrations)
                registration.Dispose();
        }
    }
}