using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.BOLT4;

using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Serialization.Interfaces;
using Infrastructure;
using Infrastructure.Bitcoin;
using Infrastructure.Protocol.Onion;
using Infrastructure.Serialization;
using Infrastructure.Serialization.Interfaces;
using Infrastructure.Serialization.Onion;

/// <summary>
/// Resolves the onion services through the same layer registrations the daemon composes, outside the Docker tests
/// (which CI skips), so a registration that only fails at daemon startup is caught here.
/// </summary>
public class OnionServiceRegistrationTests
{
    [Fact]
    public void Given_DaemonLayerRegistrations_When_ResolvingOnionServices_Then_AllResolveAsSingletons()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddInfrastructureServices();
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var hopPayloadSerializer = provider.GetRequiredService<IHopPayloadSerializer>();
        var replayCache = provider.GetRequiredService<IOnionReplayCache>();
        var sphinxService = provider.GetRequiredService<ISphinxService>();

        // Assert
        Assert.IsType<HopPayloadSerializer>(hopPayloadSerializer);
        Assert.IsType<OnionReplayCache>(replayCache);
        Assert.Same(hopPayloadSerializer, provider.GetRequiredService<IHopPayloadSerializer>());
        Assert.Same(replayCache, provider.GetRequiredService<IOnionReplayCache>());
        Assert.Same(sphinxService, provider.GetRequiredService<ISphinxService>());
        Assert.Equal(OnionReplayCache.DefaultCapacity, ((OnionReplayCache)replayCache).Capacity);
    }

    [Fact]
    public void Given_OnlySerializationRegistrations_When_ResolvingHopPayloadSerializer_Then_Resolves()
    {
        // Arrange: HopPayloadSerializer depends on ITlvConverterFactory, which used to come only from Bitcoin
        var services = new ServiceCollection();
        services.AddSerializationInfrastructureServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Act
        var hopPayloadSerializer = provider.GetRequiredService<IHopPayloadSerializer>();
        var tlvStreamSerializer = provider.GetRequiredService<ITlvStreamSerializer>();

        // Assert
        Assert.IsType<HopPayloadSerializer>(hopPayloadSerializer);
        Assert.NotNull(tlvStreamSerializer);
    }

    [Fact]
    public void Given_AllLayerRegistrations_When_ResolvingTlvConverterFactory_Then_RegisteredOnce()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddInfrastructureServices();
        services.AddSerializationInfrastructureServices();
        services.AddBitcoinInfrastructure();

        // Act
        var registrations = services.Count(d => d.ServiceType == typeof(ITlvConverterFactory));

        // Assert
        Assert.Equal(1, registrations);
    }
}