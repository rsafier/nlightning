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

    [Theory]
    [InlineData("localhost:18443")]
    [InlineData("127.0.0.1:8332")]
    [InlineData("localhost")]
    [InlineData("")]
    [InlineData("https://node.example:8332")]
    public void Given_ABitcoinEndpointTheRpcClientAccepts_When_Checked_Then_ItIsNotReported(string endpoint)
    {
        // Arrange (NL-740): NBitcoin's RPCClient adds http:// to a host[:port] and takes empty as 127.0.0.1, and nodes
        // configured that way ran before the start-up check existed
        var configuration = BuildTemplateConfiguration("regtest", ("Bitcoin:RpcEndpoint", endpoint));

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.DoesNotContain(failures, f => f.StartsWith("Bitcoin:RpcEndpoint", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ftp://localhost:18443")]
    [InlineData("localhost:notaport")]
    [InlineData("http://")]
    public void Given_ABitcoinEndpointTheRpcClientCannotUse_When_Checked_Then_ItIsReported(string endpoint)
    {
        // Arrange
        var configuration = BuildTemplateConfiguration("regtest", ("Bitcoin:RpcEndpoint", endpoint));

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Contains(failures, f => f.StartsWith("Bitcoin:RpcEndpoint", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Node:MinimumDepth", "three")]
    [InlineData("Node:ReconnectInitialDelay", "5 seconds")]
    public void Given_AValueTheBinderCannotConvert_When_Checked_Then_ItIsReportedInsteadOfThrown(string key,
                                                                                                string value)
    {
        // Arrange (NL-741): the binding generator throws while the options are first built, outside the validation
        var configuration = BuildTemplateConfiguration("regtest", (key, value));

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Contains(failures, f => f.Contains(value, StringComparison.Ordinal)
                                    || f.Contains(key.Split(':')[^1], StringComparison.Ordinal));
    }

    [Fact]
    public void Given_PollModeWithoutZmq_When_Checked_Then_ItIsValid()
    {
        // Arrange (NL-1094): a node without ZMQ (rbitcoin)
        var configuration = BuildTemplateConfiguration("regtest", ("Bitcoin:Notifications", "Poll"),
                                                       ("Bitcoin:ZmqHost", ""), ("Bitcoin:ZmqBlockPort", "0"),
                                                       ("Bitcoin:PollInterval", "00:00:03"));

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData("Bitcoin:Notifications", "Push")]
    [InlineData("Bitcoin:PollInterval", "00:00:00.100")]
    public void Given_ABadNotificationSetting_When_Checked_Then_ItIsReported(string key, string value)
    {
        // Arrange
        var configuration = key == "Bitcoin:Notifications"
                                ? BuildTemplateConfiguration("regtest", (key, value))
                                : BuildTemplateConfiguration("regtest", ("Bitcoin:Notifications", "Poll"), (key, value));

        // Act
        var failures = ConfigurationCheck.Run(configuration, "regtest");

        // Assert
        Assert.Contains(failures, f => f.Contains(key.Split(':')[^1], StringComparison.Ordinal)
                                    || f.Contains(value, StringComparison.Ordinal));
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
        Assert.Contains(failures, f => f.StartsWith("Bitcoin:RpcUser", StringComparison.Ordinal));
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