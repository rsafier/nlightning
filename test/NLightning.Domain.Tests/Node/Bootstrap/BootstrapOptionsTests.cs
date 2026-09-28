namespace NLightning.Domain.Tests.Node.Bootstrap;

using Domain.Node.Bootstrap;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;

public class BootstrapOptionsTests
{
    [Fact]
    public void Given_DefaultOptions_When_Read_Then_BootstrapIsUnsetAndValid()
    {
        // Arrange
        var options = new BootstrapOptions();

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Null(options.Enabled);
        Assert.Empty(errors);
        Assert.Equal(DnsSeedTransport.Tcp, options.Transport);
        Assert.Equal(DnsSeedAddressTypes.Both, options.AddressFamilies);
        Assert.Empty(new NodeOptions().GetValidationErrors());
    }

    [Theory]
    [InlineData("mainnet", true)]
    [InlineData("testnet", false)]
    [InlineData("regtest", false)]
    [InlineData("signet", false)]
    [InlineData("mutinynet", false)]
    public void Given_EnabledUnset_When_ReadOnANetwork_Then_OnlyMainnetBootstraps(string network, bool expected)
    {
        // Arrange: D-B10-1 as reversed by the owner on 2026-09-28
        var options = new BootstrapOptions();

        // Act
        var enabled = options.IsEnabledOn(BitcoinNetwork.Resolve(network));

        // Assert
        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData("mainnet", false)]
    [InlineData("mainnet", true)]
    [InlineData("regtest", true)]
    [InlineData("testnet", true)]
    public void Given_EnabledSet_When_ReadOnANetwork_Then_TheSettingWins(string network, bool enabled)
    {
        // Arrange
        var options = new BootstrapOptions { Enabled = enabled };

        // Act & Assert
        Assert.Equal(enabled, options.IsEnabledOn(BitcoinNetwork.Resolve(network)));
    }

    [Fact]
    public void Given_DefaultOptions_When_Read_Then_ThePublicFallbackResolversAreUsed()
    {
        // Arrange: D-B10-7, the system resolver first, then 1.1.1.1 and 8.8.8.8
        var options = new BootstrapOptions();

        // Act & Assert
        Assert.True(options.FallbackToPublicResolvers);
        Assert.Equal(["1.1.1.1", "8.8.8.8"], options.FallbackNameServers);
        Assert.Equal(BootstrapOptions.DefaultFallbackNameServers, options.FallbackNameServers);
        Assert.True(options.UsesFallbackResolvers);
    }

    [Fact]
    public void Given_ConfiguredNameServers_When_Read_Then_NoFallbackIsUsed()
    {
        // Arrange: an operator who names resolvers gets exactly those
        var options = new BootstrapOptions { NameServers = ["9.9.9.9"] };

        // Act & Assert
        Assert.False(options.UsesFallbackResolvers);
    }

    [Fact]
    public void Given_TheFallbackTurnedOffOrEmpty_When_Read_Then_NoFallbackIsUsed()
    {
        // Arrange
        var off = new BootstrapOptions { FallbackToPublicResolvers = false };
        var empty = new BootstrapOptions { FallbackNameServers = [] };

        // Act & Assert
        Assert.False(off.UsesFallbackResolvers);
        Assert.False(empty.UsesFallbackResolvers);
    }

    [Fact]
    public void Given_AnInvalidFallbackNameServer_When_Validated_Then_ItIsAnError()
    {
        // Arrange
        var options = new BootstrapOptions { FallbackNameServers = ["1.1.1.1", "resolver.example"] };

        // Act
        var errors = options.GetValidationErrors();

        // Assert
        Assert.Contains(errors, e => e.Contains("FallbackNameServers 'resolver.example'"));
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

    [Theory]
    [InlineData(true, 10, "regtest", true)]
    [InlineData(true, 15, "regtest", false)]
    [InlineData(true, 30, "regtest", false)]
    [InlineData(false, 10, "mainnet", false)]
    [InlineData(null, 10, "mainnet", false)]
    [InlineData(null, 10, "regtest", false)]
    public void Given_AConnectTimeoutAgainstTheNetworkTimeout_When_Validated_Then_ShorterIsAnErrorWhenEnabled(
        bool? enabled, int connectSeconds, string network, bool expectError)
    {
        // Arrange: the TCP connect alone may take NetworkTimeout (15 s by default). Unset is on on mainnet, but only an
        // explicit Enabled = true makes a short ConnectTimeout an error (the dial then waits NetworkTimeout)
        var nodeOptions = new NodeOptions
        {
            BitcoinNetwork = BitcoinNetwork.Resolve(network),
            Bootstrap = { Enabled = enabled, ConnectTimeout = TimeSpan.FromSeconds(connectSeconds) }
        };

        // Act
        var errors = nodeOptions.GetValidationErrors();

        // Assert
        Assert.Equal(expectError, errors.Any(e => e.Contains("Bootstrap:ConnectTimeout")));
    }

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "nlseed.nlightn.ing", "nodes.lightning.directory", "lseed.bitcoinstats.com" }, false)]
    [InlineData(new[] { "LSEED.bitcoinstats.com.", "nlseed.nlightn.ing" }, false)]
    [InlineData(new[] { "nlseed.nlightn.ing", "my.seed.example" }, true)]
    [InlineData(new[] { "host:53" }, true)]
    public void Given_AnObsoleteSeedList_When_Checked_Then_OnlyAnEditedOneCounts(string[] seeds, bool edited)
    {
        // Act & Assert
        Assert.Equal(edited, BootstrapOptions.IsEditedObsoleteSeedList(seeds));
    }

    [Fact]
    public void Given_TheMainnetSeeds_When_Read_Then_TheyAreTheLndAndClnDefaults()
    {
        // Assert (BOLT 10 names no seed list; these are what LND and CLN ship with)
        Assert.Equal(["nodes.lightning.directory", "nodes.lightning.wiki"], BootstrapOptions.MainnetSeeds);
    }

    [Theory]
    [InlineData(30, 15, 30)]
    [InlineData(30, 60, 60)]
    [InlineData(30, 30, 30)]
    public void Given_ANetworkTimeout_When_ReadingTheEffectiveConnectTimeout_Then_ItIsNeverShorter(int connectSeconds,
        int networkSeconds, int expectedSeconds)
    {
        // Arrange
        var options = new BootstrapOptions { ConnectTimeout = TimeSpan.FromSeconds(connectSeconds) };

        // Act
        var effective = options.GetEffectiveConnectTimeout(TimeSpan.FromSeconds(networkSeconds));

        // Assert
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), effective);
    }
}