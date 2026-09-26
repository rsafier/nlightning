using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace NLightning.Daemon.Utilities;

using Contracts.Constants;

public class DaemonUtils
{
    private const string DashDashDaemon = "--daemon";
    private const string DashDashDaemonChild = "--daemon-child";

    /// <summary>
    /// Shell script that starts "$0" with "$@" detached from the terminal and prints its PID.
    /// </summary>
    internal const string UnixDaemonLauncherScript = "nohup \"$0\" \"$@\" </dev/null >/dev/null 2>&1 & echo $!";

    private static readonly HashSet<string> s_bareFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        DashDashDaemon, DashDashDaemonChild, "--stop", "--status", "--help"
    };

    private static readonly HashSet<string> s_shortSwitches = ["-n", "-c", "-h", "-?"];

    public static void ShowUsage()
    {
        Console.WriteLine("NLTG - NLightning Daemon");
        Console.WriteLine("Usage:");
        Console.WriteLine("  nltg [options]");
        Console.WriteLine("  nltg --stop         Stop a running daemon");
        Console.WriteLine("  nltg --status       Show daemon status");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --network, -n <network>    Network to use (mainnet, testnet, regtest, signet, mutinynet) [default: mainnet]");
        Console.WriteLine("  --config, -c <path>        Path to custom configuration file");
        Console.WriteLine("  --daemon [true|false]      Run as a daemon [default: false]");
        Console.WriteLine("  --password-file <path>     Read the key encryption password from a file");
        Console.WriteLine("  --password-stdin           Read the key encryption password from stdin");
        Console.WriteLine("  --password <password>      Key encryption password (insecure: visible in the process list)");
        Console.WriteLine("  --stop                     Stop a running daemon");
        Console.WriteLine("  --status                   Show daemon status information");
        Console.WriteLine("  --help, -h, -?             Show this help message");
        Console.WriteLine();
        Console.WriteLine("Environment Variables:");
        Console.WriteLine("  NLTG_NETWORK               Network to use");
        Console.WriteLine("  NLTG_CONFIG                Path to custom configuration file");
        Console.WriteLine("  NLTG_DAEMON                Run as a daemon");
        Console.WriteLine("  NLTG_PASSWORD              Key encryption password");
        Console.WriteLine();
        Console.WriteLine("Configuration File:");
        Console.WriteLine("  Default path: ~/.nltg/{network}/appsettings.json");
        Console.WriteLine("  Settings:");
        Console.WriteLine("  {");
        Console.WriteLine("    \"Daemon\": true,         # Run as a background daemon");
        Console.WriteLine("    ... other settings ...");
        Console.WriteLine("  }");
        Console.WriteLine();
        Console.WriteLine("PID file location: ~/.nltg/{network}/nltg.pid");
    }

    /// <summary>
    /// Rewrites the command line so the configuration provider reads it as intended.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>-n</c> and <c>-c</c> become <c>--network</c> and <c>--config</c>.</item>
    /// <item>Bare flags become <c>--flag=true</c>, so they don't swallow the next argument as their value. An option
    /// is bare when it is a known flag, is last, or is followed by another option name (<c>--x</c> or a known short
    /// switch); values that merely start with '-' are kept.
    /// <c>--daemon true|false</c> is kept as a pair.</item>
    /// <item>Password options are dropped: they are not configuration and must not end up in it.</item>
    /// </list>
    /// </remarks>
    public static string[] NormalizeArgs(string[] args)
    {
        var normalized = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            var passwordOptionLength = PasswordUtils.GetPasswordOptionLength(arg);
            if (passwordOptionLength > 0)
            {
                i += passwordOptionLength - 1;
                continue;
            }

            if (arg == "-n")
                arg = "--network";
            else if (arg == "-c")
                arg = "--config";

            var hasNext = i + 1 < args.Length;
            if (arg.Equals(DashDashDaemon, StringComparison.OrdinalIgnoreCase)
             && hasNext && bool.TryParse(args[i + 1], out var daemonValue))
            {
                normalized.Add($"{DashDashDaemon}={daemonValue.ToString().ToLowerInvariant()}");
                i++;
                continue;
            }

            if (arg.StartsWith("--") && !arg.Contains('=')
             && (s_bareFlags.Contains(arg) || !hasNext || IsOptionName(args[i + 1])))
            {
                normalized.Add($"{arg}=true");
                continue;
            }

            normalized.Add(arg);
        }

        return normalized.ToArray();
    }

    /// <summary>
    /// Checks whether an argument names an option rather than being a value, so values that start with '-' (like
    /// negative numbers or passwords) are kept.
    /// </summary>
    private static bool IsOptionName(string arg) =>
        arg.StartsWith("--") || s_shortSwitches.Contains(arg);

    /// <summary>
    /// Checks the command line for <c>--daemon</c>, <c>--daemon=&lt;bool&gt;</c> or <c>--daemon &lt;bool&gt;</c>.
    /// </summary>
    /// <returns>The requested value, or null when the command line doesn't say.</returns>
    public static bool? GetDaemonArgument(string[] args)
    {
        bool? isDaemonRequested = null;
        foreach (var arg in NormalizeArgs(args))
        {
            if (!arg.StartsWith(DashDashDaemon + "=", StringComparison.OrdinalIgnoreCase))
                continue;

            if (bool.TryParse(arg[(DashDashDaemon.Length + 1)..], out var value))
                isDaemonRequested = value;
        }

        return isDaemonRequested;
    }

    public static bool IsStopRequested(string[] args)
    {
        return args.Any(arg =>
                            arg.Equals("--stop", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsStatusRequested(string[] args)
    {
        return args.Any(arg =>
                            arg.Equals("--status", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts the application as a daemon process if requested
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <param name="configuration">Configuration</param>
    /// <param name="pidFilePath">Path where to store the PID file</param>
    /// <param name="logger">Logger for startup messages</param>
    /// <param name="password">Key password, handed to the daemon process through its environment</param>
    /// <returns>True if the parent process should exit, false to continue execution</returns>
    public static bool StartDaemonIfRequested(string[] args, IConfiguration configuration, string pidFilePath,
                                              ILogger logger, string password)
    {
        // Check if we're already running as a daemon child process
        if (IsRunningAsDaemon())
        {
            return false; // Continue execution as a daemon child
        }

        // Check command line args (the highest priority), then the environment variable, then the config file
        var isDaemonRequested = GetDaemonArgument(args) ?? GetDaemonEnvironmentVariable()
                             ?? configuration.GetValue<bool>("Node:Daemon");

        if (!isDaemonRequested)
        {
            return false; // Continue normal execution
        }

        logger.Information("Daemon mode requested, starting background process");

        // Both paths re-exec the current program: fork() is unsafe once the .NET runtime has started threads
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                   ? StartWindowsDaemon(args, pidFilePath, logger, password)
                   : StartUnixDaemon(args, pidFilePath, logger, password);
    }

    /// <summary>
    /// Builds the daemon child's arguments: drops <c>--daemon</c> and every password option, and appends
    /// <c>--daemon-child</c>.
    /// </summary>
    public static string[] BuildDaemonChildArgs(string[] args)
    {
        var childArgs = new List<string>(args.Length + 1);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            var passwordOptionLength = PasswordUtils.GetPasswordOptionLength(arg);
            if (passwordOptionLength > 0)
            {
                i += passwordOptionLength - 1;
                continue;
            }

            if (arg.Equals(DashDashDaemon, StringComparison.OrdinalIgnoreCase))
            {
                // Also drop the value of `--daemon true|false`
                if (i + 1 < args.Length && bool.TryParse(args[i + 1], out _))
                    i++;

                continue;
            }

            if (arg.StartsWith(DashDashDaemon + "=", StringComparison.OrdinalIgnoreCase))
                continue;

            childArgs.Add(arg);
        }

        childArgs.Add(DashDashDaemonChild);
        return childArgs.ToArray();
    }

    private static bool? GetDaemonEnvironmentVariable()
    {
        var envDaemon = Environment.GetEnvironmentVariable("NLTG_DAEMON");
        if (string.IsNullOrEmpty(envDaemon))
            return null;

        return envDaemon.Equals("true", StringComparison.OrdinalIgnoreCase) || envDaemon.Equals("1");
    }

    /// <summary>
    /// Gets the program to re-exec: the apphost, or <c>dotnet &lt;app.dll&gt;</c> when started through the muxer.
    /// </summary>
    private static (string FileName, string[] PrefixArgs) GetCurrentProgram()
    {
        var processPath = Environment.ProcessPath
                       ?? throw new InvalidOperationException("Unable to determine the current process path");

        var isDotnetHost = Path.GetFileNameWithoutExtension(processPath)
                               .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return isDotnetHost ? (processPath, [Environment.GetCommandLineArgs()[0]]) : (processPath, []);
    }

    private static bool StartWindowsDaemon(string[] args, string pidFilePath, ILogger logger, string password)
    {
        try
        {
            var (fileName, prefixArgs) = GetCurrentProgram();
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };

            foreach (var arg in prefixArgs.Concat(BuildDaemonChildArgs(args)))
                startInfo.ArgumentList.Add(arg);

            startInfo.Environment[PasswordUtils.PasswordEnvironmentVariable] = password;

            // Start the new process
            var process = Process.Start(startInfo);
            if (process == null)
            {
                logger.Error("Failed to start daemon process");
                return false;
            }

            // Write PID to file
            File.WriteAllText(pidFilePath, process.Id.ToString());

            logger.Information("Daemon started with PID {PID}", process.Id);
            return true; // Parent should exit
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Error starting daemon process");
            return false;
        }
    }

    /// <summary>
    /// Starts the daemon on Linux and macOS by re-executing this program in the background under <c>nohup</c>.
    /// </summary>
    private static bool StartUnixDaemon(string[] args, string pidFilePath, ILogger logger, string password)
    {
        try
        {
            var (fileName, prefixArgs) = GetCurrentProgram();

            // "$0" "$@" passes every argument through verbatim, without any shell quoting
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(UnixDaemonLauncherScript);
            startInfo.ArgumentList.Add(fileName);
            foreach (var arg in prefixArgs.Concat(BuildDaemonChildArgs(args)))
                startInfo.ArgumentList.Add(arg);

            startInfo.Environment[PasswordUtils.PasswordEnvironmentVariable] = password;

            using var shell = Process.Start(startInfo);
            if (shell is null)
            {
                logger.Error("Failed to start daemon process");
                return false;
            }

            var pidText = shell.StandardOutput.ReadLine()?.Trim();
            shell.WaitForExit();

            if (!int.TryParse(pidText, out var pid))
            {
                logger.Error("Failed to start daemon process: no PID reported");
                return false;
            }

            File.WriteAllText(pidFilePath, pid.ToString());
            logger.Information("Daemon started with PID {PID}", pid);
            return true; // Parent should exit
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Error starting daemon process");
            return false;
        }
    }

    /// <summary>
    /// Checks if this process is already running as daemon
    /// </summary>
    public static bool IsRunningAsDaemon()
    {
        return Array.Exists(Environment.GetCommandLineArgs(),
                            arg => arg.Equals(DashDashDaemonChild, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the path for the PID file
    /// </summary>
    public static string GetPidFilePath(string configPath)
    {
        return Path.Combine(configPath, NodeConstants.PidFile);
    }

    /// <summary>
    /// Stops a running daemon if it exists
    /// </summary>
    public static bool StopDaemon(string pidFilePath, ILogger logger)
    {
        try
        {
            if (!File.Exists(pidFilePath))
            {
                logger.Warning("PID file not found, daemon may not be running");
                return false;
            }

            var pidText = File.ReadAllText(pidFilePath).Trim();
            if (!int.TryParse(pidText, out var pid))
            {
                logger.Error("Invalid PID in file: {PidText}", pidText);
                return false;
            }

            try
            {
                var process = Process.GetProcessById(pid);
                logger.Information("Stopping daemon process with PID {PID}", pid);

                // Send SIGTERM instead of Kill for graceful shutdown
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows - send Ctrl+C or use taskkill /PID {pid} /F
                    SendCtrlEvent(process);
                }
                else
                {
                    // Unix/macOS - send SIGTERM
                    SendSignal(pid, 15); // SIGTERM is 15
                }

                // Wait for exit
                var exited = process.WaitForExit(TimeSpan.FromSeconds(10));
                if (exited)
                {
                    logger.Information("Daemon process stopped successfully");
                    File.Delete(pidFilePath);
                    return true;
                }

                // If a graceful shutdown fails, force kill as last resort
                logger.Warning("Daemon process did not exit gracefully, forcing termination");
                process.Kill();
                exited = process.WaitForExit(5000);
                if (exited)
                {
                    File.Delete(pidFilePath);
                    return true;
                }

                return false;
            }
            catch (ArgumentException)
            {
                logger.Warning("No process found with PID {PID}, removing stale PID file", pid);
                File.Delete(pidFilePath);
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Error stopping daemon");
            return false;
        }
    }

    private static void SendSignal(int pid, int signal)
    {
        Process.Start("kill", $"-{signal} {pid}").WaitForExit();
    }

    private static void SendCtrlEvent(Process process)
    {
        Process.Start("taskkill", $"/PID {process.Id}").WaitForExit();
    }
}