using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Utilities;

using Daemon.Configuration;
using Daemon.Extensions;
using Daemon.Utilities;
using Domain.Enums;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;

public class VlsSigningConfigurationTests
{
    [Theory]
    [InlineData("Vls")]
    [InlineData("VLS")]
    [InlineData("vls")]
    public void VlsModeSelectsRemoteBackendWithoutSelectingNative(string mode)
    {
        var options = SigningOptions.Read(Configuration("regtest", ("Signing:Mode", mode)));

        Assert.True(options.IsVls);
        Assert.True(options.IsRemote);
        Assert.False(options.IsRemoteNative);
    }

    [Fact]
    public void VlsCompositionWithoutConnectionRefusesNativeFallback()
    {
        var services = new ServiceCollection();
        var configuration = Configuration("regtest");

        Assert.Throws<ArgumentException>(() =>
            services.AddNltgNodeServices(configuration, Mock.Of<ISecureKeyManager>()));
        Assert.Empty(services);
    }

    [Fact]
    public void OfflineVlsCheckRequiresNeitherEndpointNorCredentialNorKeyFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nltg-vls-offline-" + Guid.NewGuid().ToString("N"));
        var configuration = Configuration("regtest",
            ("Signing:SocketPath", Path.Combine(directory, "node.sock")),
            ("Signing:AuthTokenFile", Path.Combine(directory, "node.token")));

        Assert.Empty(ConfigurationCheck.Run(configuration, "regtest"));
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    public void OfflineVlsCheckRejectsUnsupportedNetworks(string network)
    {
        Assert.Equal(["The VLS prototype supports regtest only."],
            ConfigurationCheck.Run(Configuration(network), network));
    }

    [Fact]
    public void OfflineVlsCheckRejectsConfiguredNetworkThatDiffersFromTheSelectedNetwork()
    {
        Assert.Equal(["The VLS prototype supports regtest only."],
            ConfigurationCheck.Run(Configuration("mainnet"), "regtest"));
    }

    [Fact]
    public void OfflineVlsCheckPreservesPrivateKeyDependentFeatureRestrictions()
    {
        var configuration = Configuration("regtest", ("SilentPayments:Enabled", "true"));

        Assert.Equal(["Silent-payment scanning and receiving are not supported by the remote signer."],
            ConfigurationCheck.Run(configuration, "regtest"));
    }

    [Fact]
    public void CapabilityProfileOverridesUnsafeFeaturesBeforeNegotiation()
    {
        var configuration = Configuration("regtest",
            ("Node:Features:OptionSimpleTaproot", "Optional"),
            ("Node:Features:OptionSplice", "Optional"),
            ("Node:Features:DualFund", "Optional"),
            ("Node:Features:OptionProvideStorage", "Optional"),
            ("Gossip:AcceptPublicChannels", "true"),
            ("Node:Routing:FeeProportionalMillionths", "500"));
        var profiled = VlsCapabilityProfile.Apply(configuration);
        var options = profiled.GetSection("Node").Get<NodeOptions>()!;

        Assert.Equal(FeatureSupport.No, options.Features.OptionSimpleTaproot);
        Assert.Equal(FeatureSupport.No, options.Features.OptionSplice);
        Assert.Equal(FeatureSupport.No, options.Features.DualFund);
        Assert.Equal(FeatureSupport.No, options.Features.OptionProvideStorage);
        // Public channels are signed through VLS (NL-1335): the profile leaves the operator's choice alone
        Assert.True(profiled.GetValue<bool>("Gossip:AcceptPublicChannels"));
        Assert.False(profiled.GetValue<bool>("Gossip:AllowPublicChannelsOnMainnet"));
        // The profile no longer forces whole-satoshi forwarding fees
        Assert.Equal(500U, options.Routing.FeeProportionalMillionths);
        Assert.Empty(ConfigurationCheck.Run(configuration, "regtest"));
        Assert.Equal("Optional", configuration["Node:Features:OptionSimpleTaproot"]);
    }

    [Fact]
    public void CapabilityValidationRejectsFeaturesReenabledAfterTheProfile()
    {
        var options = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest };
        VlsCapabilityProfile.Apply(options);
        Assert.Empty(VlsCapabilityProfile.GetValidationErrors(options));

        options.Features.OptionSimpleTaproot = FeatureSupport.Optional;

        Assert.Contains(VlsCapabilityProfile.GetValidationErrors(options),
            message => message.Contains("disables unsupported features", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CapabilityValidationAcceptsMillisatoshiForwardingFees(bool proportional)
    {
        var options = new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest };
        VlsCapabilityProfile.Apply(options);
        if (proportional) options.Routing.FeeProportionalMillionths = 1;
        else options.Routing.FeeBaseMsat = 1_001;

        Assert.Empty(VlsCapabilityProfile.GetValidationErrors(options));
    }

    private static IConfiguration Configuration(string network, params (string Key, string Value)[] overrides)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            NodeConfigurationExtensions.CreateDefaultConfigJson(network)));
        var values = new Dictionary<string, string?>
        {
            ["Signing:Mode"] = "Vls",
            ["Signing:SocketPath"] = "/nonexistent/nltg-vls/node.sock",
            ["Signing:AuthTokenFile"] = "/nonexistent/nltg-vls/node.token"
        };
        foreach (var (key, value) in overrides) values[key] = value;
        return new ConfigurationBuilder().AddJsonStream(stream).AddInMemoryCollection(values).Build();
    }
}