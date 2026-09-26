using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.Constants;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Entities.Channel;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Interceptors;
using Infrastructure.Persistence.Providers;

public class PersistenceConfigurationTests
{
    [Theory]
    [InlineData("postgres", "Host=localhost;Database=nlightning")]
    [InlineData("sqlite", "Data Source=:memory:")]
    [InlineData("sqlserver", "Server=localhost;Database=nlightning")]
    public void Given_SensitiveQueryLoggingNotConfigured_When_ResolvingDbContextOptions_Then_SensitiveLoggingIsOff(
        string provider, string connectionString)
    {
        // Arrange
        using var serviceProvider = BuildServiceProvider(provider, connectionString, null);

        // Act
        var options = serviceProvider.GetRequiredService<DbContextOptions<NLightningDbContext>>();

        // Assert
        Assert.False(options.FindExtension<CoreOptionsExtension>()?.IsSensitiveDataLoggingEnabled ?? false);
    }

    [Theory]
    [InlineData("postgres", "Host=localhost;Database=nlightning")]
    [InlineData("sqlite", "Data Source=:memory:")]
    [InlineData("sqlserver", "Server=localhost;Database=nlightning")]
    public void Given_SensitiveQueryLoggingEnabled_When_ResolvingDbContextOptions_Then_SensitiveLoggingIsOn(
        string provider, string connectionString)
    {
        // Arrange
        using var serviceProvider = BuildServiceProvider(provider, connectionString, "true");

        // Act
        var options = serviceProvider.GetRequiredService<DbContextOptions<NLightningDbContext>>();

        // Assert
        Assert.True(options.FindExtension<CoreOptionsExtension>()?.IsSensitiveDataLoggingEnabled ?? false);
    }

    [Fact]
    public async Task Given_SqliteProvider_When_TheNodeOpensAConnection_Then_SynchronousIsFull()
    {
        // Arrange (N5-T3: a committed transition must survive a power loss before its message is sent)
        var path = Path.Combine(Path.GetTempPath(), $"nltg-durability-{Guid.NewGuid():N}.db");
        try
        {
            await using var serviceProvider = BuildServiceProvider("sqlite", $"Data Source={path};Pooling=False", null);
            await using var scope = serviceProvider.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();

            // Act
            await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            var synchronous = await ReadSynchronousAsync(context.Database.GetDbConnection());
            await context.Database.CloseConnectionAsync();

            // Assert
            Assert.Equal(2L, synchronous);
            var interceptors = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()!
                                      .Interceptors;
            Assert.Contains(interceptors!, i => i is SqliteDurabilityInterceptor);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Given_ConnectionWithSynchronousOff_When_TheDurabilityInterceptorRuns_Then_SynchronousIsFull()
    {
        // Arrange: the setting is per connection, so a connection string or build could lower it
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA synchronous=OFF;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(0L, await ReadSynchronousAsync(connection));

        // Act
        await new SqliteDurabilityInterceptor().ConnectionOpenedAsync(connection, null!,
                                                                      TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2L, await ReadSynchronousAsync(connection));
    }

    private static async Task<long> ReadSynchronousAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Given_SqlServerModel_When_InspectingChannelRemoteNodeId_Then_ColumnFitsACompactPubKey()
    {
        // Arrange
        using var context = CreateSqlServerContext();

        // Act
        var columnType = context.GetService<IDesignTimeModel>().Model
                                .FindEntityType(typeof(ChannelEntity))!
                                .FindProperty(nameof(ChannelEntity.RemoteNodeId))!
                                .GetColumnType();

        // Assert
        Assert.Equal($"varbinary({CryptoConstants.CompactPubkeyLen})", columnType);
    }

    [Theory]
    [InlineData("postgres", "Host=localhost;Database=nlightning")]
    [InlineData("sqlite", "Data Source=:memory:")]
    [InlineData("sqlserver", "Server=localhost;Database=nlightning")]
    public void Given_ProviderMigrations_When_ComparedToTheModel_Then_ThereAreNoPendingModelChanges(
        string provider, string connectionString)
    {
        // Arrange
        using var serviceProvider = BuildServiceProvider(provider, connectionString, null);
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();

        // Act
        var hasPendingChanges = context.Database.HasPendingModelChanges();

        // Assert
        Assert.False(hasPendingChanges);
    }

    [Theory]
    [InlineData("postgres", "Host=localhost;Database=nlightning")]
    [InlineData("sqlite", "Data Source=:memory:")]
    [InlineData("sqlserver", "Server=localhost;Database=nlightning")]
    public void Given_ProviderMigrations_When_DiffingEachDesignerModelWithThePreviousOne_Then_OnlyTheMigrationChangesAppear(
        string provider, string connectionString)
    {
        // Arrange: a Designer (target model) that disagrees with its migration makes EF diff the wrong schema for
        // every later migration (F6: WidenWatchedTransactionIndex still had RemoteNodeId as varbinary(32))
        using var serviceProvider = BuildServiceProvider(provider, connectionString, null);
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var differ = context.GetService<IMigrationsModelDiffer>();
        var modelInitializer = context.GetService<IModelRuntimeInitializer>();
        var activeProvider = context.Database.ProviderName!;

        IRelationalModel? previousModel = null;
        var mismatches = new List<string>();

        // Act
        foreach (var (id, type) in migrationsAssembly.Migrations)
        {
            var migration = migrationsAssembly.CreateMigration(type, activeProvider);
            var targetModel = FinalizeModel(modelInitializer, migration.TargetModel!).GetRelationalModel();

            // Hand-written SQL is invisible to the model differ
            var expected = migration.UpOperations.Where(o => o is not SqlOperation).Select(Describe).Order().ToList();
            var actual = differ.GetDifferences(previousModel, targetModel).Select(Describe).Order().ToList();
            if (!expected.SequenceEqual(actual))
                mismatches.Add($"{id}: migration [{string.Join(", ", expected)}] vs designer diff " +
                               $"[{string.Join(", ", actual)}]");

            previousModel = targetModel;
        }

        var snapshotModel = FinalizeModel(modelInitializer, migrationsAssembly.ModelSnapshot!.Model)
           .GetRelationalModel();

        // Assert
        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
        Assert.False(differ.HasDifferences(previousModel, snapshotModel));
    }

    private static IModel FinalizeModel(IModelRuntimeInitializer modelInitializer, IModel model)
    {
        if (model is IMutableModel mutableModel)
            model = mutableModel.FinalizeModel();

        return modelInitializer.Initialize(model, designTime: true);
    }

    private static string Describe(MigrationOperation operation)
    {
        var table = operation is ITableMigrationOperation tableOperation ? tableOperation.Table : null;
        var name = operation.GetType().GetProperty("Name")?.GetValue(operation) as string;
        var columnType = operation is ColumnOperation columnOperation ? columnOperation.ColumnType : null;
        return $"{operation.GetType().Name}({table}.{name} {columnType})".Replace(" )", ")");
    }

    private static NLightningDbContext CreateSqlServerContext()
    {
        var options = new DbContextOptionsBuilder<NLightningDbContext>()
                     .UseSqlServer("Server=localhost;Database=nlightning",
                                   x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.SqlServer"))
                     .Options;

        return new NLightningDbContext(options, new DatabaseTypeProvider(DatabaseType.MicrosoftSql));
    }

    private static ServiceProvider BuildServiceProvider(string provider, string connectionString,
                                                       string? enableSensitiveQueryLogging)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = provider,
            ["Database:ConnectionString"] = connectionString
        };
        if (enableSensitiveQueryLogging is not null)
            settings["Database:EnableSensitiveQueryLogging"] = enableSensitiveQueryLogging;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddPersistenceInfrastructureServices(configuration);

        return services.BuildServiceProvider();
    }
}