using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Offers.Receive;

using Application.Offers;
using Application.Offers.Receive;

/// <summary>
/// NL-743: the <c>appsettings.json</c> template wrote <c>Offers:PathLifetimeMarginBlocks</c> 144 into every node's file
/// before NL-719/NL-723 raised the default to 1,008, so the raised default never reached existing nodes.
/// </summary>
public class OfferOptionsUpgradeTests
{
    [Theory]
    [InlineData(OfferOptions.FormerDefaultPathLifetimeMarginBlocks, OfferOptions.DefaultPathLifetimeMarginBlocks)]
    [InlineData(145u, 145u)]
    [InlineData(2_016u, 2_016u)]
    public void Given_AConfiguredMargin_When_TheOptionsAreBuilt_Then_OnlyTheFormerTemplateDefaultIsRaised(
        uint configured, uint expected)
    {
        // Arrange
        var services = new ServiceCollection();
        services.Configure<OfferOptions>(o => o.PathLifetimeMarginBlocks = configured);
        services.AddOffersServices();
        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<OfferOptions>>().Value;

        // Assert
        Assert.Equal(expected, options.PathLifetimeMarginBlocks);
        Assert.Equal(configured != expected, options.PathLifetimeMarginRaisedFromFormerDefault);
    }
}
