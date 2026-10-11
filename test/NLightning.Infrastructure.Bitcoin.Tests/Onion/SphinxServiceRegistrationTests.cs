using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Crypto.Functions;
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
        var ecdh = new Ecdh();
        var nodeKey = ecdh.GenerateKeyPair();
        var keyManager = new EcdhOnlyKeyManager(nodeKey.PrivKey);
        var services = new ServiceCollection();
        services.AddSingleton<ISecureKeyManager>(keyManager);
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider();
        var sphinxService = provider.GetRequiredService<ISphinxService>();
        var packet = sphinxService.Construct([new OnionHop(nodeKey.CompactPubKey, new byte[] { 0x02, 0x00 })],
                                             ecdh.GenerateKeyPair().PrivKey, ReadOnlySpan<byte>.Empty);

        // Act
        var peeled = sphinxService.PeelAsLocalNode(packet, ReadOnlySpan<byte>.Empty);

        // Assert
        Assert.True(peeled.IsFinal);
        Assert.Equal(1, keyManager.EcdhCalls);
    }
}