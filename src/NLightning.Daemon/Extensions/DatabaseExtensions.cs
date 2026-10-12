using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Extensions;

using Infrastructure.Persistence.Contexts;

public static class DatabaseExtensions
{
    /// <summary>
    /// Runs database migrations if configured to do so
    /// </summary>
    public static Task MigrateDatabaseIfConfiguredAsync(this IHost host) =>
        MigrateDatabaseIfConfiguredAsync(host.Services);

    /// <summary>
    /// Runs database migrations if configured to do so, on a node service provider (the host's, or the throwaway one
    /// a locked start checks the delivered key with, NL-1349)
    /// </summary>
    public static async Task MigrateDatabaseIfConfiguredAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        // Check if migrations should run
        var runMigrations = configuration.GetValue("Database:RunMigrations", false);

        if (!runMigrations)
        {
            logger.LogInformation("Database migrations are disabled in configuration");
            return;
        }

        // EF Core refuses migrations under NativeAOT ("Design-time DbContext operations are not supported"), compiled
        // model or not; a NativeAOT build never gets here (Program stops it first, NL-708)
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new PlatformNotSupportedException(
                "EF Core migrations cannot run in a NativeAOT build (NL-708); apply them with the JIT build or an SQL "
              + "script (dotnet ef migrations script --idempotent).");

        try
        {
            var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();

            // Check if there are pending migrations
            var pendingMigrations = (await context.Database.GetPendingMigrationsAsync()).ToList();

            if (pendingMigrations.Count > 0)
            {
                logger.LogInformation("Found {Count} pending migrations. Applying...", pendingMigrations.Count);
                await context.Database.MigrateAsync();
                logger.LogInformation("Database migrations completed successfully");
            }
            else
            {
                logger.LogInformation("Database is up to date, no migrations needed");
            }

            await EnableSnapshotReadsAsync(context, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while applying database migrations");
            throw;
        }
    }

    /// <summary>
    /// SQL Server only: allows SNAPSHOT isolation, which the channel loads read under (NL-810); without it they read
    /// query by query and may see a save half-way. Refused (no ALTER DATABASE permission) is logged, not fatal.
    /// </summary>
    private static async Task EnableSnapshotReadsAsync(NLightningDbContext context, ILogger logger)
    {
        if (!context.Database.IsSqlServer())
            return;

        try
        {
            await context.EnableSqlServerSnapshotIsolationAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not allow snapshot isolation on the SQL Server database; channel loads will "
                               + "read without a snapshot (run ALTER DATABASE ... SET ALLOW_SNAPSHOT_ISOLATION ON)");
        }
    }
}