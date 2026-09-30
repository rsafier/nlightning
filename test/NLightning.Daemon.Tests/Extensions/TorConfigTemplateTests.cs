using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Extensions;

using Daemon.Extensions;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Transport.Tor;

/// <summary>
/// The <c>Node:Tor</c> section of the default <c>appsettings.json</c> and the Tor services of the node graph.
/// </summary>
public class TorConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_TorIsBound_Then_EveryKeyIsAnOptionAndEqualsItsDefault(string network)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                           .Build();
        var section = configuration.GetSection(TorOptions.SectionName);
        var defaults = new TorOptions();
        var properties = typeof(TorOptions).GetProperties().Where(p => p.CanWrite).ToList();

        // Act
        var bound = section.Get<TorOptions>();

        // Assert: a typo would bind nothing, a missing key would hide a knob from the operator
        Assert.NotNull(bound);
        var keys = section.GetChildren().Select(c => c.Key).Order().ToList();
        Assert.Equal(properties.Select(p => p.Name).Order().ToList(), keys);
        foreach (var property in properties)
            Assert.Equal(property.GetValue(defaults), property.GetValue(bound));
        Assert.Equal(TorMode.Off, bound.Mode);
        Assert.Empty(bound.GetValidationErrors());
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheOnionServiceIsTheAnnouncedAddressSource()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Network"] = "regtest",
                               ["Node:Tor:Mode"] = "TorOnly",
                               ["Database:Provider"] = "Sqlite",
                               ["Database:ConnectionString"] = "Data Source=:memory:"
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var onion = provider.GetRequiredService<ITorOnionService>();
        var sources = provider.GetServices<IAnnouncedAddressSource>().ToList();

        // Assert
        Assert.IsType<TorOnionService>(onion);
        Assert.Same(onion, Assert.Single(sources));
        Assert.IsType<TorSocksDialer>(provider.GetRequiredService<ITorSocksDialer>());
        Assert.Equal(TorMode.TorOnly,
                     provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NodeOptions>>().Value.Tor.Mode);
    }
}