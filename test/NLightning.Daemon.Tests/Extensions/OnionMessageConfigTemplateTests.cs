using System.Text;
using Microsoft.Extensions.Configuration;

namespace NLightning.Daemon.Tests.Extensions;

using Application.OnionMessages;
using Daemon.Extensions;

/// <summary>
/// The <c>OnionMessages</c> section of the default <c>appsettings.json</c> (wave M6): every key binds to a real
/// <see cref="OnionMessageOptions"/> property, the values are the code defaults, and they are valid.
/// </summary>
public class OnionMessageConfigTemplateTests
{
    [Theory]
    [InlineData("mainnet")]
    [InlineData("testnet")]
    [InlineData("signet")]
    [InlineData("regtest")]
    public void Given_DefaultConfigJson_When_OnionMessagesIsBound_Then_EveryKeyIsAnOptionAndEqualsItsDefault(
        string network)
    {
        // Arrange
        var json = NodeConfigurationExtensions.CreateDefaultConfigJson(network);
        var configuration = new ConfigurationBuilder()
                           .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
                           .Build();
        var section = configuration.GetSection(OnionMessageOptions.SectionName);
        var defaults = new OnionMessageOptions();
        var properties = typeof(OnionMessageOptions).GetProperties().Where(p => p.CanWrite).ToList();

        // Act
        var bound = section.Get<OnionMessageOptions>();

        // Assert: a typo would bind nothing, a missing key would hide a knob from the operator
        Assert.NotNull(bound);
        var keys = section.GetChildren().Select(c => c.Key).Order().ToList();
        Assert.Equal(properties.Select(p => p.Name).Order().ToList(), keys);
        foreach (var property in properties)
            Assert.Equal(property.GetValue(defaults), property.GetValue(bound));
        Assert.Empty(bound.GetValidationErrors());
    }
}