using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Infrastructure.Bitcoin.Onion;

public class SphinxServiceRegistrationTests
{
    [Fact]
    public void Given_BitcoinInfrastructure_When_ResolvingSphinxServiceWithoutKeyManager_Then_ReturnsSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider();

        // Act
        var first = provider.GetRequiredService<ISphinxService>();
        var second = provider.GetRequiredService<ISphinxService>();

        // Assert
        Assert.IsType<SphinxService>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Given_BitcoinInfrastructureWithKeyManager_When_ResolvingSphinxService_Then_KeyManagerIsInjected()
    {
        // Arrange
        var keyManager = new Mock<ISecureKeyManager>();
        var services = new ServiceCollection();
        services.AddSingleton(keyManager.Object);
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider();
        var sphinxService = provider.GetRequiredService<ISphinxService>();

        // Act: the key manager is consulted before the packet is touched, so a default packet reaches it
        Assert.ThrowsAny<Exception>(() => sphinxService.PeelAsLocalNode(default, ReadOnlySpan<byte>.Empty));

        // Assert
        keyManager.Verify(m => m.GetNodeKeyPair(), Times.Once);
    }
}