using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.TestUtils;

using Application.Channels.Services;
using Domain.Channels.Interfaces;

/// <summary>
/// The shared test double <see cref="FakeServiceProvider"/> follows the <see cref="IServiceProvider"/> contract
/// (NL-249).
/// </summary>
public class FakeServiceProviderTests
{
    [Fact]
    public void Given_AnUnregisteredType_When_GettingTheService_Then_NullIsReturned()
    {
        // Arrange
        var provider = new FakeServiceProvider();

        // Act
        var htlcSwitch = provider.GetService<IHtlcSwitch>();
        var eventQueue = provider.GetService<ChannelDomainEventQueue>();

        // Assert
        Assert.Null(htlcSwitch);
        Assert.Null(eventQueue);
    }

    [Fact]
    public void Given_AnUnregisteredType_When_GettingTheRequiredService_Then_InvalidOperationExceptionIsThrown()
    {
        // Arrange
        var provider = new FakeServiceProvider();

        // Act & Assert - the same exception the real provider throws
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IHtlcSwitch>());
    }

    [Fact]
    public void Given_AStrictProvider_When_GettingAnUnregisteredType_Then_KeyNotFoundExceptionIsThrown()
    {
        // Arrange
        var provider = new FakeServiceProvider { Strict = true };

        // Act & Assert
        Assert.Throws<KeyNotFoundException>(() => provider.GetService<IHtlcSwitch>());
    }

    [Fact]
    public void Given_ARegisteredService_When_ResolvedFromTheProviderOrAScope_Then_TheSameInstanceIsReturned()
    {
        // Arrange
        var provider = new FakeServiceProvider();
        var htlcSwitch = new Mock<IHtlcSwitch>().Object;
        provider.AddService(typeof(IHtlcSwitch), htlcSwitch);

        // Act
        using var scope = provider.CreateScope();

        // Assert
        Assert.Same(htlcSwitch, provider.GetRequiredService<IHtlcSwitch>());
        Assert.Same(htlcSwitch, scope.ServiceProvider.GetRequiredService<IHtlcSwitch>());
        Assert.Same(provider, provider.GetService<IServiceProvider>());
    }
}