using Serilog;

namespace NLightning.Daemon.Utilities;

/// <summary>
/// Resolves the key encryption password from the sources that do not expose it in the process list.
/// </summary>
public static class PasswordUtils
{
    public const string PasswordEnvironmentVariable = "NLTG_PASSWORD";
    public const string DashDashPassword = "--password";
    public const string DashDashPasswordFile = "--password-file";
    public const string DashDashPasswordStdin = "--password-stdin";

    /// <summary>
    /// Gets the password without prompting.
    /// </summary>
    /// <remarks>
    /// Precedence: <c>--password-file &lt;path&gt;</c>, <c>--password-stdin</c>, <c>--password &lt;value&gt;</c>
    /// (logs a warning, because the value is visible in the process list), then the <c>NLTG_PASSWORD</c>
    /// environment variable.
    /// </remarks>
    /// <returns>The password, or null when none of the sources provides one.</returns>
    public static string? ResolvePassword(string[] args, TextReader stdin, ILogger logger)
    {
        var passwordFile = GetOptionValue(args, DashDashPasswordFile);
        if (passwordFile is not null)
            return TrimLineEnding(File.ReadAllText(passwordFile));

        if (args.Any(arg => arg.Equals(DashDashPasswordStdin, StringComparison.OrdinalIgnoreCase)))
            return TrimLineEnding(stdin.ReadLine() ?? string.Empty);

        var password = GetOptionValue(args, DashDashPassword);
        if (password is not null)
        {
            logger.Warning(
                "{Option} exposes the password in the process list. Use {EnvironmentVariable}, {FileOption} or {StdinOption} instead",
                DashDashPassword, PasswordEnvironmentVariable, DashDashPasswordFile, DashDashPasswordStdin);
            return password;
        }

        var envPassword = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        return string.IsNullOrEmpty(envPassword) ? null : envPassword;
    }

    /// <summary>
    /// Checks whether an argument is one of the password options.
    /// </summary>
    /// <returns>1 when the option carries its value in the same argument or takes none, 2 when the value is the next
    /// argument, and 0 when it is not a password option.</returns>
    public static int GetPasswordOptionLength(string arg)
    {
        if (arg.Equals(DashDashPassword, StringComparison.OrdinalIgnoreCase)
         || arg.Equals(DashDashPasswordFile, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (arg.StartsWith(DashDashPassword + "=", StringComparison.OrdinalIgnoreCase)
         || arg.StartsWith(DashDashPasswordFile + "=", StringComparison.OrdinalIgnoreCase)
         || arg.Equals(DashDashPasswordStdin, StringComparison.OrdinalIgnoreCase))
            return 1;

        return 0;
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : null;

            if (args[i].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(option.Length + 1)..];
        }

        return null;
    }

    private static string TrimLineEnding(string value) => value.TrimEnd('\r', '\n');
}