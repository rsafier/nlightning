namespace NLightning.Daemon.Contracts.Helpers;

/// <summary>
/// Helper class for displaying command line usage information
/// </summary>
public static class CommandLineHelper
{
    public const string DashH = "-h";
    public const string DashQuestion = "-?";
    public const string DashDashHelp = "--help";
    public const string DashN = "-n";
    public const string DashDashNetwork = "--network";
    public const string DashDashNetworkEquals = "--network=";
    public const string DashC = "-c";
    public const string DashDashCookie = "--cookie";
    public const string DashDashCookieEquals = "--cookie=";

    public const string NetworkEnvironmentVariable = "NLTG_NETWORK";
    public const string CookieEnvironmentVariable = "NLTG_COOKIE";

    /// <summary>
    /// Parse command line arguments to check for help request
    /// </summary>
    public static bool IsHelpRequested(string[] args)
    {
        return args.Any(arg =>
                            arg.Equals(DashDashHelp, StringComparison.OrdinalIgnoreCase)
                         || arg.Equals(DashH, StringComparison.OrdinalIgnoreCase)
                         || arg.Equals(DashQuestion, StringComparison.Ordinal));
    }

    public static string? GetCommand(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (IsOptionWithSeparateValue(args[i]))
            {
                // Skip the option's value
                i++;
                continue;
            }

            if (IsOption(args[i]))
                continue;

            return args[i].ToLowerInvariant();
        }

        return null;
    }

    public static string[] GetCommandArguments(string command, string[] args)
    {
        var cmdArgs = new List<string>();
        var cmdFound = false;

        for (var i = 0; i < args.Length; i++)
        {
            // GetCookiePath reads these options anywhere, so they are never command arguments
            if (IsOptionWithSeparateValue(args[i]))
            {
                // Skip the option's value, so it is never mistaken for the command or one of its arguments
                i++;
                continue;
            }

            if (IsOptionWithInlineValue(args[i]))
                continue;

            if (!cmdFound)
            {
                if (args[i].Equals(command, StringComparison.OrdinalIgnoreCase))
                    cmdFound = true;

                continue;
            }

            cmdArgs.Add(args[i]);
        }

        return cmdArgs.ToArray();
    }

    /// <summary>
    /// Resolves the directory that holds the cookie file.
    /// </summary>
    /// <remarks>
    /// Precedence: <c>--cookie</c>/<c>-c</c>, then <c>--network</c>/<c>-n</c>, then <c>NLTG_COOKIE</c>, then
    /// <c>NLTG_NETWORK</c>, then <c>~/.nltg/mainnet</c>.
    /// </remarks>
    public static string GetCookiePath(string[] args)
    {
        string? network = null;
        string? cookiePath = null;

        // Check command line args
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals(DashDashNetwork, StringComparison.OrdinalIgnoreCase) || arg.Equals(DashN))
            {
                if (i + 1 < args.Length)
                    network ??= args[++i];
            }
            else if (arg.StartsWith(DashDashNetworkEquals, StringComparison.OrdinalIgnoreCase))
            {
                network ??= arg[DashDashNetworkEquals.Length..];
            }
            else if (arg.Equals(DashDashCookie, StringComparison.OrdinalIgnoreCase) || arg.Equals(DashC))
            {
                if (i + 1 < args.Length)
                    cookiePath ??= args[++i];
            }
            else if (arg.StartsWith(DashDashCookieEquals, StringComparison.OrdinalIgnoreCase))
            {
                cookiePath ??= arg[DashDashCookieEquals.Length..];
            }
        }

        // Command line args take precedence over the environment
        if (string.IsNullOrEmpty(cookiePath) && string.IsNullOrEmpty(network))
        {
            var envCookie = Environment.GetEnvironmentVariable(CookieEnvironmentVariable);
            if (!string.IsNullOrEmpty(envCookie))
            {
                cookiePath = envCookie;
            }
            else
            {
                var envNetwork = Environment.GetEnvironmentVariable(NetworkEnvironmentVariable);
                if (!string.IsNullOrEmpty(envNetwork))
                    network = envNetwork;
            }
        }

        if (!string.IsNullOrEmpty(cookiePath))
            return ExtractDirectoryFromCookiePath(cookiePath);

        // Go with the default path if no cookie path was provided
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        cookiePath = Path.Combine(homeDir, ".nltg", string.IsNullOrEmpty(network) ? "mainnet" : network);
        return Directory.Exists(cookiePath) ? cookiePath : throw new InvalidOperationException("Cookie not found");
    }

    private static string ExtractDirectoryFromCookiePath(string cookiePath)
    {
        cookiePath = Path.GetFullPath(cookiePath);
        if (cookiePath.EndsWith(".cookie", StringComparison.OrdinalIgnoreCase))
        {
            cookiePath = Path.GetDirectoryName(cookiePath) ??
                         throw new InvalidOperationException("Cookie not found");
        }
        else if (cookiePath.EndsWith(Path.DirectorySeparatorChar))
            cookiePath = cookiePath[..^1];

        return Directory.Exists(cookiePath) ? cookiePath : throw new InvalidOperationException("Cookie not found");
    }

    private static bool IsOptionWithSeparateValue(string arg) =>
        arg.Equals(DashN)
     || arg.Equals(DashDashNetwork, StringComparison.OrdinalIgnoreCase)
     || arg.Equals(DashC)
     || arg.Equals(DashDashCookie, StringComparison.OrdinalIgnoreCase);

    private static bool IsOptionWithInlineValue(string arg) =>
        arg.StartsWith(DashDashNetworkEquals, StringComparison.OrdinalIgnoreCase)
     || arg.StartsWith(DashDashCookieEquals, StringComparison.OrdinalIgnoreCase);

    private static bool IsOption(string arg) => arg.StartsWith('-');
}