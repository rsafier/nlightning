using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Extensions;

using Application.Payments.Switch;
using Application.Payments.Trampoline;
using Daemon.Extensions;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-875 TR3-T1: the <c>Node:Trampoline</c> section (<see cref="TrampolineOptions"/>) is in the default
/// <c>appsettings.json</c> on every network with every code default, binds through the daemon's composition, and an
/// invalid one fails the start validation; the relay engine is composed as the switch's handler (TR3-T2).
/// </summary>
public class TrampolineConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("mutinynet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_Bound_Then_EveryKeyIsTheCodeDefault(string network)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                                                      .Build();
        var section = configuration.GetSection(TrampolineOptions.SectionName);
        var defaults = new TrampolineOptions();

        // Act
        var bound = section.Get<TrampolineOptions>();

        // Assert: every settable property is written, at its default
        Assert.NotNull(bound);
        foreach (var property in typeof(TrampolineOptions).GetProperties().Where(p => p.CanWrite))
        {
            Assert.True(section.GetSection(property.Name).Exists(), $"{property.Name} is missing from the template");
            Assert.Equal(property.GetValue(defaults), property.GetValue(bound));
        }
    }

    [Fact]
    public void Given_ANodeTrampolineSection_When_Composed_Then_TheOptionsAreBound()
    {
        // Arrange
        using var provider = BuildProvider(("Node:Trampoline:FeeBaseMsat", "2000"),
                                           ("Node:Trampoline:CltvExpiryDelta", "288"),
                                           ("Node:Trampoline:LegTimeout", "00:00:30"));

        // Act
        var options = provider.GetRequiredService<IOptions<TrampolineOptions>>().Value;

        // Assert
        Assert.Equal(2_000u, options.FeeBaseMsat);
        Assert.Equal((ushort)288, options.CltvExpiryDelta);
        Assert.Equal(TimeSpan.FromSeconds(30), options.LegTimeout);
        Assert.Equal(1_000u, options.FeeProportionalMillionths);
    }

    [Fact]
    public void Given_AMarginAboveTheDelta_When_Validated_Then_TheStartFails()
    {
        // Arrange
        using var provider = BuildProvider(("Node:Trampoline:CltvExpiryDelta", "40"),
                                           ("Node:Trampoline:MinCltvMarginBlocks", "48"));

        // Act
        var exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<TrampolineOptions>>().Value);

        // Assert
        Assert.Contains(exception.Failures, f => f.Contains("MinCltvMarginBlocks"));
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheRelayEngineIsTheSwitchHandlerAndTheLegObserver()
    {
        // Arrange
        using var provider = BuildProvider();

        // Act
        var engine = provider.GetRequiredService<TrampolineRelayService>();

        // Assert
        Assert.Same(engine, provider.GetRequiredService<ITrampolineHtlcHandler>());
        Assert.Same(engine, provider.GetRequiredService<ITrampolineLegObserver>());
        using var scope = provider.CreateScope();
        Assert.Same(engine, scope.ServiceProvider.GetRequiredService<ITrampolineRelayIngress>());
    }

    private static ServiceProvider BuildProvider(params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Network"] = "regtest",
                               ["Database:Provider"] = "Sqlite",
                               ["Database:ConnectionString"] = "Data Source=:memory:"
                           })
                           .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}