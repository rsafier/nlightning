using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Persistence;

using CompiledModels;
using Contexts;
using Enums;
using Interceptors;
using Providers;

/// <summary>
/// Extension methods for setting up Persistence infrastructure services in an IServiceCollection.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds Bitcoin infrastructure services to the specified IServiceCollection.
    /// </summary>
    /// <param name="services">The IServiceCollection to add services to.</param>
    /// <param name="configuration">The IConfiguration instance to read configuration settings from.</param>
    /// <returns>The same service collection so that multiple calls can be chained.</returns>
    public static IServiceCollection AddPersistenceInfrastructureServices(this IServiceCollection services,
                                                                          IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var dbConfigSection = configuration.GetSection("Database");
        var providerName = dbConfigSection["Provider"]?.ToLowerInvariant();
        var connectionString = dbConfigSection["ConnectionString"];

        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new InvalidOperationException("Database provider ('Database:Provider') is not configured.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Database connection string ('Database:ConnectionString') is not configured.");
        }

        var resolvedDatabaseType = providerName.ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => DatabaseType.PostgreSql,
            "sqlite" => DatabaseType.Sqlite,
            "sqlserver" or "microsoftsql" => DatabaseType.MicrosoftSql,
            _ => throw new InvalidOperationException($"Unsupported database provider configured: {providerName}")
        };

        services.AddSingleton(new DatabaseTypeProvider(resolvedDatabaseType));

        // The compiled model (NL-708): always under NativeAOT, which cannot build a model at run time; in the JIT build
        // only when Database:UseCompiledModel is true (the model is then not built at start)
        var useCompiledModel = CompiledModelCatalog.ShouldUse(ParseOptionalBool(dbConfigSection["UseCompiledModel"]));

        services.AddDbContext<NLightningDbContext>((_, optionsBuilder) =>
        {
            // Check if we should be logging sensible data (i.e., query values)
            if ((configuration["Database:EnableSensitiveQueryLogging"]?.ToLowerInvariant() ?? "false") == "true")
                optionsBuilder.EnableSensitiveDataLogging();

            if (useCompiledModel)
                optionsBuilder.UseModel(CompiledModelCatalog.Get(resolvedDatabaseType));

            switch (resolvedDatabaseType)
            {
                case DatabaseType.PostgreSql:
                    const string pgMigrationsAssembly = "NLightning.Infrastructure.Persistence.Postgres";
                    optionsBuilder.UseNpgsql(connectionString, sqlOptions =>
                                   {
                                       sqlOptions.MigrationsAssembly(pgMigrationsAssembly);
                                   })
                                  .UseSnakeCaseNamingConvention();
                    break;

                case DatabaseType.Sqlite:
                    const string sqliteMigrationsAssembly = "NLightning.Infrastructure.Persistence.Sqlite";
                    optionsBuilder.UseSqlite(connectionString, sqlOptions =>
                                  {
                                      sqlOptions.MigrationsAssembly(sqliteMigrationsAssembly);
                                  })
                                  .AddInterceptors(new SqliteDurabilityInterceptor());
                    break;

                case DatabaseType.MicrosoftSql:
                    const string sqlServerMigrationsAssembly = "NLightning.Infrastructure.Persistence.SqlServer";
                    optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
                    {
                        sqlOptions.MigrationsAssembly(sqlServerMigrationsAssembly);
                    });
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported database provider configured: {providerName}");
            }
        });

        return services;
    }

    private static bool? ParseOptionalBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return bool.TryParse(value, out var parsed)
                   ? parsed
                   : throw new InvalidOperationException(
                         $"Database:UseCompiledModel must be true or false, not '{value}'.");
    }
}