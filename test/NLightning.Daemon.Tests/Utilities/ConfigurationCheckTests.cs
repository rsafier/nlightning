using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Tests.Utilities;

using Daemon.Extensions;
using Daemon.Utilities;

/// <summary>
/// <c>nltg --check-config</c> (NL-338): the node's start-up options validation without a key, bitcoind or database.
/// </summary>
public class ConfigurationCheckTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    [InlineData("regtest")]
    public void Given_TheDefaultConfiguration_When_Checked_Then_ItIsValid(string network)
    {
        // Arrange
        var configuration = BuildTemplateConfiguration(network);

        // Act
        var failures = ConfigurationCheck.Run(configuration, Resolve(network));

        // Assert
        Assert.Empty(failures);
    }

    [Fact]
    public void Given_ABitcoinEndpointWithoutScheme_When_Checked_Then_ItIsReported()
    {
        // Arrange
        var configuration = BuildTemplateConfiguration("regtest", ("Bitcoin:RpcEndpoint", "localhost:18443"));

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Contains(failures, f => f.StartsWith("Bitcoin:RpcEndpoint", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_AMissingBitcoinSection_When_Checked_Then_EveryRequiredSettingIsReported()
    {
        // Arrange: no Bitcoin section at all (the members used to be C# required, NL-338)
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Network"] = "regtest",
                               ["Database:Provider"] = "Sqlite",
                               ["Database:ConnectionString"] = "Data Source=:memory:"
                           })
                           .Build();

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Contains(failures, f => f.StartsWith("Bitcoin:RpcEndpoint", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("Bitcoin:ZmqBlockPort", StringComparison.Ordinal));
    }

    [Fact]
    public void Given_NoDatabaseProvider_When_Checked_Then_ItIsReported()
    {
        // Arrange
        var configuration = BuildTemplateConfiguration("regtest", ("Database:Provider", ""));

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Contains(failures, f => f.Contains("Database:Provider", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Node:DustLimitAmount")]
    [InlineData("Node:HtlcMinimumAmount")]
    [InlineData("Node:MinimumChannelSize")]
    public void Given_AnAmountTheConfigurationCannotSet_When_Checked_Then_ItIsReportedOnce(string key)
    {
        // Arrange: the binder leaves LightningMoney members alone, so a value would be ignored silently
        var configuration = BuildTemplateConfiguration("regtest", (key, "500"));

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Single(failures, f => f.StartsWith(key, StringComparison.Ordinal));
    }

    private static string Resolve(string network) => network == "mutinynet" ? "signet" : network;

    private static IConfiguration BuildTemplateConfiguration(string network, params (string Key, string Value)[] extra)
    {
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var builder = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        if (extra.Length > 0)
            builder.AddInMemoryCollection(extra.ToDictionary(e => e.Key, string? (e) => e.Value));

        return builder.Build();
    }
}