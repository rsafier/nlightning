using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Node.PeerStorage;

using Application.Node.PeerStorage;
using Domain.Channels.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Protocol.Interfaces;

public class PeerStorageServiceCollectionExtensionsTests
{
    [Fact]
    public void Given_TheRegistration_When_Resolved_Then_OneServiceBehindBothTypesWithTheDefaultProvider()
    {
        // Arrange
        var services = CreateHostServices();

        // Act
        services.AddPeerStorageServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        // Assert
        Assert.Same(provider.GetRequiredService<PeerStorageService>(),
                    provider.GetRequiredService<IPeerStorageService>());
        Assert.IsType<ChannelListPeerBackupBlobProvider>(provider.GetRequiredService<IPeerBackupBlobProvider>());
    }

    [Fact]
    public void Given_ABlobProviderRegisteredFirst_When_Registered_Then_ItIsKept()
    {
        // Arrange: the static channel backup lane's own provider
        var services = CreateHostServices();
        var own = new Mock<IPeerBackupBlobProvider>().Object;
        services.AddSingleton(own);

        // Act
        services.AddPeerStorageServices();
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Same(own, provider.GetRequiredService<IPeerBackupBlobProvider>());
    }

    private static ServiceCollection CreateHostServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddOptions<NodeOptions>();
        services.AddSingleton(new Mock<IChannelMemoryRepository>().Object);
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        return services;
    }
}