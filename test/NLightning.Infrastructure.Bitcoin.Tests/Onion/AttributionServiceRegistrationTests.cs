using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Protocol.Onion.Interfaces;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Onion;

public class AttributionServiceRegistrationTests
{
    [Fact]
    public void Given_BitcoinInfrastructureAndAttributionServices_When_Resolving_Then_ReturnsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IFailureMessageSerializer>().Object);
        services.AddBitcoinInfrastructure();
        services.AddOnionAttributionServices();
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredService<IAttributionDataService>();
        var second = provider.GetRequiredService<IAttributionDataService>();

        // Assert
        Assert.IsType<AttributionDataService>(first);
        Assert.Same(first, second);
    }
}