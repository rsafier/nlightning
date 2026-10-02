using Microsoft.Data.Sqlite;
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
    [InlineData("--check-config", "--network", "regtest")]
    [InlineData("--network", "regtest", "--check-config")]
    public void GivenArgs_WhenNormalizedAndBound_ThenNetworkIsRead(params string[] args)
    {
        // Act
        var config = new ConfigurationBuilder().AddCommandLine(DaemonUtils.NormalizeArgs(args)).Build();

        // Assert
        Assert.Equal("regtest", config["network"]);
        Assert.Null(config["password"]);
    }

    [Theory]
    [InlineData(true, "--check-config")]
    [InlineData(true, "--network", "regtest", "--check-config")]
    [InlineData(false, "--status")]
    [InlineData(false)]
    public void Given_Args_When_CheckingForCheckConfig_Then_OnlyTheFlagRequestsIt(bool expected, params string[] args)
    {
        // Act / Assert (NL-338)
        Assert.Equal(expected, DaemonUtils.IsCheckConfigRequested(args));
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
    public void GivenNoConfig_WhenReadInitialConfiguration_ThenDirectoryAndFileAreOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange: the file holds the bitcoind RPC password and the directory the key file, cookie and database
        Environment.SetEnvironmentVariable("HOME", _tempHome);

        // Act
        var (_, _, configPath) = NodeConfigurationExtensions.ReadInitialConfiguration(["-n", "regtest"]);

        // Assert
        var mode = OperatingSystem.IsWindows() ? UnixFileMode.None : File.GetUnixFileMode(configPath);
        var fileMode = OperatingSystem.IsWindows()
                           ? UnixFileMode.None
                           : File.GetUnixFileMode(Path.Combine(configPath, "appsettings.json"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
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
    public void Given_TheTemplatesRelativePaths_When_ReadInitialConfiguration_Then_TheyResolveAgainstTheConfigDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange: a custom configuration directory whose file carries the template's relative paths. NL-306: the
        // database, the logs and the fee cache used to land in whatever directory the daemon was started from.
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        var configDir = Path.Combine(_tempHome, "configs", "regtest");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "appsettings.json"),
                          NodeConfigurationExtensions.CreateDefaultConfigJson("regtest"));

        // Act
        var (config, _, _) = NodeConfigurationExtensions.ReadInitialConfiguration(["--config", configDir]);

        // Assert: the relative paths are anchored to the configuration directory (the template's File sink is the
        // second entry of Serilog's WriteTo array, flattened as index 1)
        var dataSource = new SqliteConnectionStringBuilder(config["Database:ConnectionString"]).DataSource;
        Assert.Equal(Path.Combine(configDir, "nltg.db"), dataSource);
        Assert.Equal(Path.Combine(configDir, "logs", "log-.txt"), config["Serilog:WriteTo:1:Args:path"]);
        Assert.Equal(Path.Combine(configDir, "fee_estimation_cache.bin"), config["FeeEstimation:CacheFile"]);
        Assert.Equal(Path.Combine(configDir, "tor_onion_v3.key"), config["Node:Tor:OnionServiceKeyFile"]);
        // The financial books' price file (NL-602 A3-T2), also when the file does not name it
        Assert.Equal(Path.Combine(configDir, "prices.csv"), config["Accounting:Prices:CsvFile"]);
    }

    [Fact]
    public void Given_AFileWithoutTheOnionKeyPath_When_ReadInitialConfiguration_Then_TheDefaultKeySitsInTheConfigDirectory()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange: an older file without a Node:Tor section, and one with an absolute key path
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        var configDir = Path.Combine(_tempHome, "configs", "regtest-tor");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "appsettings.json"), "{ \"Node\": { \"Network\": \"regtest\" } }");
        var absoluteDir = Path.Combine(_tempHome, "configs", "regtest-tor-absolute");
        Directory.CreateDirectory(absoluteDir);
        File.WriteAllText(Path.Combine(absoluteDir, "appsettings.json"),
                          "{ \"Node\": { \"Network\": \"regtest\", \"Tor\": { \"OnionServiceKeyFile\": \"/keys/onion.key\" } } }");

        // Act
        var (config, _, _) = NodeConfigurationExtensions.ReadInitialConfiguration(["--config", configDir]);
        var (absolute, _, _) = NodeConfigurationExtensions.ReadInitialConfiguration(["--config", absoluteDir]);

        // Assert: the key is the onion address, so it never lands in the working directory
        Assert.Equal(Path.Combine(configDir, "tor_onion_v3.key"), config["Node:Tor:OnionServiceKeyFile"]);
        Assert.Equal("/keys/onion.key", absolute["Node:Tor:OnionServiceKeyFile"]);
    }

    [Fact]
    public void Given_AbsoluteAndSpecialSqlitePaths_When_ReadInitialConfiguration_Then_TheyAreLeftAlone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses HOME to redirect the default config dir");

        // Arrange
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        var configDir = Path.Combine(_tempHome, "configs", "regtest-anchored");
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "appsettings.json"), """
            {
              "Database": {
                "Provider": "Sqlite",
                "ConnectionString": "Data Source=:memory:;Cache=Shared"
              },
              "Serilog": {
                "WriteTo": [
                  { "Name": "File", "Args": { "path": "/var/log/nltg/log-.txt" } }
                ]
              }
            }
            """);

        // Act
        var (config, _, _) = NodeConfigurationExtensions.ReadInitialConfiguration(["--config", configDir]);

        // Assert: an absolute path and SQLite's :memory: are not rewritten
        Assert.Equal("Data Source=:memory:;Cache=Shared", config["Database:ConnectionString"]);
        Assert.Equal("/var/log/nltg/log-.txt", config["Serilog:WriteTo:0:Args:path"]);
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