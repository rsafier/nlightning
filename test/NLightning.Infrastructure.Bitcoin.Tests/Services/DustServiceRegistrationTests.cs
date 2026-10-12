using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin.Tests.Services;

using Bitcoin.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Protocol.Interfaces;

public class DustServiceRegistrationTests
{
    [Fact]
    public void Given_BitcoinInfrastructureWithFeeService_When_ResolvingDustService_Then_ReturnsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IFeeService>().Object);
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetService<IDustService>();
        var second = provider.GetService<IDustService>();

        // Assert
        Assert.IsType<DustService>(first);
        Assert.Same(first, second);
    }
}