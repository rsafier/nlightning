using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace NLightning.Daemon.Tests.Extensions;

using Application.Offers.Receive;
using Daemon.Extensions;
using Daemon.Services;

/// <summary>
/// The <c>Offers</c> section of the default <c>appsettings.json</c> (NL-454): every key binds to a real
/// <see cref="OfferOptions"/> property, the values are the code defaults, and they are valid. Also the expired BOLT 12
/// invoice pruning's hosted service (NL-448).
/// </summary>
public class OfferConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_OffersIsBound_Then_EveryKeyIsAnOptionAndEqualsItsDefault(string network)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                           .Build();
        var section = configuration.GetSection(OfferOptions.SectionName);
        var defaults = new OfferOptions();
        var properties = typeof(OfferOptions).GetProperties().Where(p => p.CanWrite).ToList();

        // Act
        var bound = section.Get<OfferOptions>();

        // Assert: a typo would bind nothing, a missing key would hide a knob from the operator
        Assert.NotNull(bound);
        var keys = section.GetChildren().Select(c => c.Key).Order().ToList();
        Assert.Equal(properties.Select(p => p.Name).Order().ToList(), keys);
        foreach (var property in properties)
            Assert.Equal(property.GetValue(defaults), property.GetValue(bound));
        Assert.Empty(bound.GetValidationErrors());
    }

    [Fact]
    public void Given_PruningRegisteredTwice_When_Resolved_Then_OneHostedService()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddExpiredBolt12InvoicePruning();
        services.AddExpiredBolt12InvoicePruning();

        // Assert
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService)
                                  && d.ImplementationType == typeof(ExpiredBolt12InvoicePruneHostedService));
    }
}