using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Crypto.Constants;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Entities.Channel;
using Infrastructure.Persistence.Enums;
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

    [Fact]
    public void Given_SqlServerMigrations_When_ComparedToTheModel_Then_ThereAreNoPendingModelChanges()
    {
        // Arrange
        using var context = CreateSqlServerContext();

        // Act
        var hasPendingChanges = context.Database.HasPendingModelChanges();

        // Assert
        Assert.False(hasPendingChanges);
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