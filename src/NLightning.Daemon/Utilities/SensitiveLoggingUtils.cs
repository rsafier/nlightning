using Microsoft.Extensions.Configuration;
using Serilog;

namespace NLightning.Daemon.Utilities;

/// <summary>
/// Startup checks for settings that make secrets reach the logs (SECURITY_REVIEW SR-12).
/// </summary>
public static class SensitiveLoggingUtils
{
    public const string SensitiveQueryLoggingKey = "Database:EnableSensitiveQueryLogging";

    /// <summary>
    /// Logs a warning when <c>Database:EnableSensitiveQueryLogging</c> is on: EF Core then logs query parameter
    /// values, which include payment preimages and per-commitment secrets.
    /// </summary>
    /// <returns>True when the setting is on (the warning was logged).</returns>
    public static bool WarnIfSensitiveQueryLoggingEnabled(IConfiguration configuration, ILogger logger)
    {
        // Read as the persistence layer does (AddPersistenceInfrastructureServices): only "true" turns it on
        if (!string.Equals(configuration[SensitiveQueryLoggingKey], "true", StringComparison.OrdinalIgnoreCase))
            return false;

        logger.Warning(
            "{Setting} is on: database query parameters are logged, and they include payment preimages and " +
            "per-commitment secrets. Turn it off outside debugging and delete the logs written while it was on",
            SensitiveQueryLoggingKey);
        return true;
    }
}