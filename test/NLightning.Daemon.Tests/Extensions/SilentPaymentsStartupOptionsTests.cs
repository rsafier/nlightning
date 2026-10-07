using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Extensions;

using Daemon.Extensions;
using Domain.Bitcoin.SilentPayments;
using Domain.Protocol.Interfaces;

public class SilentPaymentsStartupOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("mainnet")]
    [InlineData("MAINNET")]
    [InlineData(" mainnet ")]
    [InlineData("")]
    [InlineData("  ")]
    public void Given_EnabledMainnetConfiguration_When_RealNodeOptionsBind_Then_MainnetGateFailsClosed(string? network)
    {
        // Arrange
        using var provider = Build(network, new Dictionary<string, string?> { ["SilentPayments:Enabled"] = "true" });
        // Act
        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SilentPaymentsOptions>>().Value);
        // Assert
        Assert.Contains("AllowMainnet", error.Message);
    }

    [Theory]
    [InlineData("regtest", false)]
    [InlineData("signet", false)]
    [InlineData("mutinynet", false)]
    [InlineData("testnet4", false)]
    [InlineData("mainnet", true)]
    public void Given_AllowedConfiguration_When_RealNodeOptionsBind_Then_SilentPaymentsCanStart(string network, bool allowMainnet)
    {
        // Arrange
        using var provider = Build(network, new Dictionary<string, string?>
        {
            ["SilentPayments:Enabled"] = "true",
            ["SilentPayments:AllowMainnet"] = allowMainnet.ToString()
        });
        // Act
        var options = provider.GetRequiredService<IOptions<SilentPaymentsOptions>>().Value;
        // Assert
        Assert.True(options.Enabled);
        Assert.Empty(options.GetValidationErrors(network == "mainnet"));
    }

    [Fact]
    public void Given_DisabledMainnetConfiguration_When_RealNodeOptionsBind_Then_ValidWithoutPermission()
    {
        // Arrange
        using var provider = Build("mainnet", []);
        // Act
        var options = provider.GetRequiredService<IOptions<SilentPaymentsOptions>>().Value;
        // Assert
        Assert.False(options.Enabled);
        Assert.False(options.AllowMainnet);
    }

    [Theory]
    [InlineData("PrevoutSource", "999")]
    [InlineData("MinSendSat", "-1")]
    [InlineData("MinReceiveSat", "-1")]
    [InlineData("MaxLabels", "100001")]
    [InlineData("RecoveryLabelCount", "100001")]
    public void Given_UnsafeConfiguration_When_RealNodeOptionsBind_Then_StartupValidationRejects(string field, string value)
    {
        // Arrange
        using var provider = Build("regtest", new Dictionary<string, string?> { [$"SilentPayments:{field}"] = value });
        // Act / Assert
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<SilentPaymentsOptions>>().Value);
    }

    private static ServiceProvider Build(string? network, Dictionary<string, string?> values)
    {
        if (network is not null)
            values["Node:Network"] = network;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, Mock.Of<ISecureKeyManager>());
        return services.BuildServiceProvider();
    }
}