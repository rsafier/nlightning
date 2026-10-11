using Npgsql;

namespace NLightning.Integration.Tests.Persistence;

using Infrastructure.Persistence;

/// <summary>
/// NL-1404: a Postgres connection string without <c>No Reset On Close</c> gets it on (no <c>DISCARD ALL</c> each time
/// EF takes a pooled connection); one that sets it keeps its value.
/// </summary>
public class PostgresConnectionDefaultsTests
{
    [Fact]
    public void Given_AConnectionStringWithoutTheKey_When_Configured_Then_TheResetIsOff()
    {
        // Act
        var configured = DependencyInjection.WithPostgresDefaults("Host=127.0.0.1;Database=nltg;Username=u");

        // Assert
        var builder = new NpgsqlConnectionStringBuilder(configured);
        Assert.True(builder.NoResetOnClose);
        Assert.Equal("nltg", builder.Database);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;No Reset On Close=false")]
    [InlineData("Host=127.0.0.1;NoResetOnClose=false")]
    [InlineData("Host=127.0.0.1;no reset on close=False")]
    public void Given_AConnectionStringThatSetsTheKey_When_Configured_Then_ItIsKept(string connectionString)
    {
        // Act
        var configured = DependencyInjection.WithPostgresDefaults(connectionString);

        // Assert
        Assert.Equal(connectionString, configured);
        Assert.False(new NpgsqlConnectionStringBuilder(configured).NoResetOnClose);
    }
}