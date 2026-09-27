using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Node.PeerStorage;

using Application.Node.PeerStorage;
using Domain.Channels.Interfaces;
using Domain.Enums;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Gossip.Sync;

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

    [Fact]
    public async Task Given_ADelayedWrite_When_StoppedBeforeTheProviderIsDisposed_Then_ItIsWritten()
    {
        // Arrange: the production registration in a real container, as the daemon composes it
        var store = new InMemoryPeerStorageDbRepository();
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.PeerStorageDbRepository).Returns(store);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() => store.SaveAsync());
        var services = CreateHostServices();
        services.Configure<NodeOptions>(o => o.Features.OptionProvideStorage = FeatureSupport.Optional);
        services.Configure<PeerStorageOptions>(o => o.StoreWithoutChannel = true);
        services.AddScoped(_ => unitOfWork.Object);
        services.AddPeerStorageServices();
        var provider = services.BuildServiceProvider();
        var storage = provider.GetRequiredService<PeerStorageService>();
        var peer = new FakeGossipPeer(50);
        storage.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 1 })));
        await storage.LastWork;
        storage.HandleMessage(peer, new PeerStorageMessage(new PeerStoragePayload(new byte[] { 2 })));

        // Act: the host stops the service, then disposes the container
        await storage.StopAsync();
        await provider.DisposeAsync();

        // Assert: the delayed blob was written while scopes could still be created
        Assert.Equal(new byte[] { 2 }, store.GetSaved(peer.PeerPubKey)!.Blob);
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