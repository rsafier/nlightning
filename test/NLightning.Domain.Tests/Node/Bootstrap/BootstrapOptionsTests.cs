namespace NLightning.Domain.Tests.Node.Bootstrap;

using Domain.Node.Bootstrap;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

public class BootstrapOptionsTests
{
    [Fact]
    public void Given_DefaultOptions_When_Read_Then_BootstrapIsOffAndValid()
    {
        // Arrange
        var options = new BootstrapOptions();

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Null(options.Enabled);
        Assert.False(options.IsEnabled);
        Assert.Empty(errors);
        Assert.Equal(DnsSeedTransport.Tcp, options.Transport);
        Assert.Equal(DnsSeedAddressTypes.Both, options.AddressFamilies);
        Assert.Empty(new NodeOptions().GetValidationErrors());
    }

    [Theory]
    [InlineData("mainnet", 2)]
    [InlineData("testnet", 1)]
    [InlineData("regtest", 0)]
    [InlineData("signet", 0)]
    [InlineData("mutinynet", 0)]
    public void Given_ANetwork_When_ReadingTheDefaultSeeds_Then_OnlyMainnetAndTestnetHaveSeeds(string network,
        int expected)
    {
        // Arrange
        var options = new BootstrapOptions();
        var resolved = BitcoinNetwork.Resolve(network);

        // Act
        var defaults = BootstrapOptions.GetDefaultSeeds(resolved);
        var effective = options.GetEffectiveSeeds(resolved, out var ignored);

        // Assert
        Assert.Equal(expected, defaults.Count);
        Assert.Equal(expected, effective.Count);
        Assert.False(ignored);
    }

    [Fact]
    public void Given_MainnetDefaults_When_Read_Then_TheyAreTheLightningDirectoryAndWiki()
    {
        // Act
        var seeds = BootstrapOptions.GetDefaultSeeds(BitcoinNetwork.Mainnet);

        // Assert
        Assert.Equal(["nodes.lightning.directory", "nodes.lightning.wiki"], seeds);
        Assert.Equal(["test.nodes.lightning.directory"], BootstrapOptions.GetDefaultSeeds(BitcoinNetwork.Testnet));
    }

    [Theory]
    [InlineData("regtest")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    public void Given_ConfiguredSeedsOnANetworkWithoutSeeds_When_Resolved_Then_TheyAreIgnoredUnlessAllowed(
        string network)
    {
        // Arrange
        var resolved = BitcoinNetwork.Resolve(network);
        var options = new BootstrapOptions { Seeds = ["seed.example.org"] };
        var allowed = new BootstrapOptions { Seeds = ["seed.example.org"], AllowSeedsOnThisNetwork = true };

        // Act
        var effective = options.GetEffectiveSeeds(resolved, out var ignored);
        var effectiveAllowed = allowed.GetEffectiveSeeds(resolved, out var ignoredAllowed);

        // Assert
        Assert.Empty(effective);
        Assert.True(ignored);
        Assert.Equal(["seed.example.org"], effectiveAllowed);
        Assert.False(ignoredAllowed);
    }

    [Fact]
    public void Given_ConfiguredSeedsOnMainnet_When_Resolved_Then_TheyReplaceTheDefaults()
    {
        // Arrange
        var options = new BootstrapOptions { Seeds = ["Seed.Example.Org.", "seed.example.org", " "] };

        // Act
        var effective = options.GetEffectiveSeeds(BitcoinNetwork.Mainnet, out _);

        // Assert
        Assert.Equal(["seed.example.org"], effective);
    }

    public static TheoryData<Action<BootstrapOptions>, string> InvalidOptions => new()
    {
        { o => o.MinPeers = 0, "MinPeers" },
        { o => o.MaxPeersFromBootstrap = -1, "MaxPeersFromBootstrap" },
        { o => o.MaxPerSeed = 0, "MaxPerSeed" },
        { o => o.MaxPerSeed = 101, "MaxPerSeed" },
        { o => o.MaxDialConcurrency = 0, "MaxDialConcurrency" },
        { o => o.MaxDialConcurrency = 9, "MaxDialConcurrency" },
        { o => o.MaxRuns = 0, "MaxRuns" },
        { o => o.PerSeedTimeout = TimeSpan.Zero, "PerSeedTimeout" },
        { o => o.QueryTimeout = TimeSpan.FromSeconds(-1), "QueryTimeout" },
        { o => o.ConnectTimeout = TimeSpan.Zero, "ConnectTimeout" },
        { o => o.RetryInterval = TimeSpan.Zero, "RetryInterval" },
        { o => o.StartupDelay = TimeSpan.FromSeconds(-1), "StartupDelay" },
        { o => o.Seeds = ["bad_seed..example"], "Seeds" },
        { o => o.Seeds = [new string('a', 64) + ".example.org"], "Seeds" },
        { o => o.Seeds = ["-bad.example.org"], "Seeds" },
        { o => o.NameServers = ["not-an-ip"], "NameServers" },
        { o => o.NameServers = ["1.1.1.1:99999"], "NameServers" },
        { o => o.AddressFamilies = 0, "AddressFamilies" }
    };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Given_AnInvalidValue_When_Validated_Then_TheErrorNamesTheKey(Action<BootstrapOptions> change,
                                                                             string key)
    {
        // Arrange
        var options = new BootstrapOptions();
        change(options);
        var nodeOptions = new NodeOptions { Bootstrap = options };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains($"Bootstrap:{key}"));
        Assert.Contains(nodeOptions.GetValidationErrors(), e => e.Contains($"Bootstrap:{key}"));
    }

    [Theory]
    [InlineData("1.1.1.1", "1.1.1.1", 53)]
    [InlineData("8.8.8.8:5353", "8.8.8.8", 5353)]
    [InlineData("2606:4700:4700::1111", "2606:4700:4700::1111", 53)]
    [InlineData("[2606:4700:4700::1111]:853", "2606:4700:4700::1111", 853)]
    public void Given_ANameServer_When_Parsed_Then_TheAddressAndPortAreRead(string value, string address, int port)
    {
        // Act
        var parsed = BootstrapOptions.TryParseNameServer(value, out var endPoint);

        // Assert
        Assert.True(parsed);
        Assert.Equal(System.Net.IPAddress.Parse(address), endPoint.Address);
        Assert.Equal(port, endPoint.Port);
    }
}