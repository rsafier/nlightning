using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.InteractiveTx;

using Application.InteractiveTx;
using Application.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Interfaces;
using TestDoubles;

public class InteractiveTxServiceCollectionExtensionsTests
{
    [Fact]
    public void Given_NoBitcoinSideServices_When_BuiltWithValidation_Then_TheProviderStillBuilds()
    {
        // Arrange (the builder, inspector and contributor are lane IT-B's; a ValidateOnBuild host must still start)
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Act
        services.AddInteractiveTxServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // Assert
        Assert.IsType<DomainInteractiveTxEngine>(provider.GetRequiredService<IInteractiveTxEngine>());
    }

    [Fact]
    public void Given_TheBitcoinSideServices_When_Resolved_Then_OneDriverForEveryChannel()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<IInteractiveTxBuilder, FakeInteractiveTxBuilder>();
        services.AddSingleton<IPrevTxInspector, FakePrevTxInspector>();
        services.AddSingleton<IInteractiveTxContributor, FakeInteractiveTxContributor>();

        // Act
        services.AddInteractiveTxServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Assert
        var driver = provider.GetRequiredService<IInteractiveTxDriver>();
        Assert.IsType<InteractiveTxDriver>(driver);
        Assert.Same(driver, provider.GetRequiredService<IInteractiveTxDriver>());
    }
}