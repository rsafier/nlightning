using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;

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