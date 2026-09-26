using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Tests.Utilities;

using Daemon.Extensions;
using Daemon.Utilities;
using TestCollections;

[Collection(SerialTestCollection.Name)]
public class DaemonArgsTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string? _originalHome;

    public DaemonArgsTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"nltg-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHome);
        _originalHome = Environment.GetEnvironmentVariable("HOME");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        Directory.Delete(_tempHome, true);
    }

    [Theory]
    [InlineData("-n", "regtest")]
    [InlineData("--network", "regtest")]
    [InlineData("--daemon", "--network", "regtest")]
    [InlineData("--network", "regtest", "--daemon")]
    [InlineData("--daemon-child", "--network", "regtest")]
    [InlineData("--password", "secret", "--network", "regtest")]
    [InlineData("--password-stdin", "--network", "regtest")]
    public void GivenArgs_WhenNormalizedAndBound_ThenNetworkIsRead(params string[] args)
    {
        // Act
        var config = new ConfigurationBuilder().AddCommandLine(DaemonUtils.NormalizeArgs(args)).Build();

        // Assert
        Assert.Equal("regtest", config["network"]);
        Assert.Null(config["password"]);
    }

    [Fact]
    public void GivenShortConfigSwitch_WhenNormalizedAndBound_ThenConfigIsRead()
    {
        // Act
        var config = new ConfigurationBuilder().AddCommandLine(DaemonUtils.NormalizeArgs(["-c", "/tmp/x.json"]))
                                               .Build();

        // Assert
        Assert.Equal("/tmp/x.json", config["config"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true, "--daemon")]
    [InlineData(true, "--daemon", "--network", "regtest")]
    [InlineData(true, "--daemon=true")]
    [InlineData(true, "--daemon", "true")]
    [InlineData(false, "--daemon", "false")]
    [InlineData(false, "--daemon=false")]
    public void GivenDaemonArgs_WhenGetDaemonArgument_ThenReturnsRequestedValue(bool? expected, params string[] args)
    {
        // Act
        var result = DaemonUtils.GetDaemonArgument(args);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GivenNetworkWithoutConfig_WhenReadInitialConfiguration_ThenConfigNetworkMatchesDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange
        Environment.SetEnvironmentVariable("HOME", _tempHome);

        // Act
        var (config, network, configPath) = NodeConfigurationExtensions.ReadInitialConfiguration(["-n", "testnet"]);

        // Assert
        Assert.Equal("testnet", network);
        Assert.Equal(Path.Combine(_tempHome, ".nltg", "testnet"), configPath);
        Assert.Equal("testnet", config["Node:Network"]);
        var writtenConfig = new ConfigurationBuilder().AddJsonFile(Path.Combine(configPath, "appsettings.json"))
                                                      .Build();
        Assert.Equal("testnet", writtenConfig["Node:Network"]);
    }

    [Fact]
    public void GivenExistingConfigWithOtherNetwork_WhenReadInitialConfiguration_ThenThrowsInsteadOfOverriding()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        var dir = Path.Combine(_tempHome, ".nltg", "mainnet");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                          NodeConfigurationExtensions.CreateDefaultConfigJson("regtest"));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
                                                                     NodeConfigurationExtensions
                                                                        .ReadInitialConfiguration([]));

        // Assert
        Assert.Contains("'regtest'", exception.Message);
        Assert.Contains("'mainnet'", exception.Message);
    }

    [Fact]
    public void GivenExistingConfigWithoutNetwork_WhenReadInitialConfiguration_ThenDirectoryNetworkIsUsed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        var dir = Path.Combine(_tempHome, ".nltg", "testnet");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{ "Node": { "Daemon": false } }""");

        // Act
        var (config, network, _) = NodeConfigurationExtensions.ReadInitialConfiguration(["--network", "testnet"]);

        // Assert
        Assert.Equal("testnet", network);
        Assert.Equal("testnet", config["Node:Network"]);
    }

    [Fact]
    public void GivenMutinynet_WhenReadInitialConfiguration_ThenSignetInItsOwnDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange
        Environment.SetEnvironmentVariable("HOME", _tempHome);

        // Act: twice, the second run reads the file the first one wrote
        var (_, firstNetwork, _) = NodeConfigurationExtensions.ReadInitialConfiguration(["--network", "mutinynet"]);
        var (config, network, configPath) =
            NodeConfigurationExtensions.ReadInitialConfiguration(["--network", "MutinyNet"]);

        // Assert
        Assert.Equal("signet", firstNetwork);
        Assert.Equal("signet", network);
        Assert.Equal(Path.Combine(_tempHome, ".nltg", "mutinynet"), configPath);
        Assert.Equal("signet", config["Node:Network"]);
        Assert.Equal("mutinynet", config["Node:CustomSignet:Name"]);
        Assert.Equal("https://mutinynet.com/api/v1/fees/recommended", config["FeeEstimation:Url"]);
    }

    [Fact]
    public void GivenCustomSignetNamedInItsDirectoryFile_WhenReadInitialConfiguration_ThenItResolvesToSignet()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange: a signet only this node's configuration knows
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        var dir = Path.Combine(_tempHome, ".nltg", "myteamsignet");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "appsettings.json"),
                          """{ "Node": { "Network": "signet", "CustomSignet": { "Name": "myteamsignet" } } }""");

        try
        {
            // Act
            var (config, network, _) = NodeConfigurationExtensions.ReadInitialConfiguration(["-n", "myteamsignet"]);

            // Assert
            Assert.Equal("signet", network);
            Assert.Equal("signet", config["Node:Network"]);
            Assert.Equal("myteamsignet", config["Node:CustomSignet:Name"]);
        }
        finally
        {
            Domain.Protocol.ValueObjects.BitcoinNetwork.Unregister("myteamsignet");
        }
    }

    [Fact]
    public void GivenUnknownNetwork_WhenReadInitialConfiguration_ThenItFailsWithoutCreatingADirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange
        Environment.SetEnvironmentVariable("HOME", _tempHome);

        // Act
        var exception = Assert.Throws<ArgumentException>(() => NodeConfigurationExtensions
                                                                  .ReadInitialConfiguration(["-n", "testnet4"]));

        // Assert: no fallback to mainnet and nothing written for the typo
        Assert.Contains("testnet4", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(_tempHome, ".nltg", "testnet4")));
    }

    [Fact]
    public void GivenPasswordEnvironmentVariable_WhenReadInitialConfiguration_ThenPasswordIsNotInConfiguration()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        var originalPassword = Environment.GetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable);
        Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, "env-secret");

        try
        {
            // Act
            var (config, _, _) = NodeConfigurationExtensions.ReadInitialConfiguration(["-n", "regtest"]);

            // Assert
            Assert.Null(config["PASSWORD"]);
            Assert.DoesNotContain(config.AsEnumerable(), pair => pair.Value == "env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(PasswordUtils.PasswordEnvironmentVariable, originalPassword);
        }
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("-s3cret")]
    [InlineData("-pw")]
    public void GivenOptionValueStartingWithDash_WhenNormalizedAndBound_ThenValueAndLaterOptionsAreKept(string value)
    {
        // Arrange
        string[] args = ["--Bitcoin:RpcPassword", value, "--network", "regtest"];

        // Act
        var config = new ConfigurationBuilder().AddCommandLine(DaemonUtils.NormalizeArgs(args)).Build();

        // Assert
        Assert.Equal(value, config["Bitcoin:RpcPassword"]);
        Assert.Equal("regtest", config["network"]);
    }

    [Theory]
    [InlineData("--network")]
    [InlineData("-n")]
    [InlineData("-c")]
    public void GivenUnknownFlagFollowedByOption_WhenNormalized_ThenFlagIsBare(string nextOption)
    {
        // Act
        var result = DaemonUtils.NormalizeArgs(["--verbose", nextOption, "x"]);

        // Assert
        Assert.Equal("--verbose=true", result[0]);
    }
}