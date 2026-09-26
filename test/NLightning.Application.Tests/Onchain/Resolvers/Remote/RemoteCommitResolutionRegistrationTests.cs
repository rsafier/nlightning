using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Remote;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class RemoteCommitResolutionRegistrationTests
{
    [Fact]
    public void Given_NodeServices_When_ResolverResolvedInAScope_Then_ScopedWithSharedMemory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ICommitmentOutputMapper>().Object);
        services.AddSingleton(new Mock<ISweepTransactionBuilder>().Object);
        services.AddSingleton(new Mock<ILightningSigner>().Object);
        services.AddSingleton(new Mock<IFeeService>().Object);
        services.AddSingleton(new Mock<IChainBroadcaster>().Object);
        services.AddSingleton(new Mock<IOutpointWatcher>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        services.AddSingleton(new Mock<IHtlcSwitch>().Object);
        services.AddScoped(_ => new Mock<IBitcoinWalletService>().Object);

        // Act
        services.AddRemoteCommitResolutionServices();
        services.AddRemoteCommitResolutionServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();
        var first = scope1.ServiceProvider.GetRequiredService<IRemoteCommitResolver>();
        var second = scope2.ServiceProvider.GetRequiredService<IRemoteCommitResolver>();

        // Assert
        Assert.IsType<RemoteCommitResolver>(first);
        Assert.NotSame(first, second);
        Assert.Same(first, scope1.ServiceProvider.GetRequiredService<IRemoteCommitResolver>());
        Assert.IsType<WalletRemoteSweepDestination>(scope1.ServiceProvider
                                                          .GetRequiredService<IRemoteSweepDestination>());
        Assert.Same(scope1.ServiceProvider.GetRequiredService<RemoteResolutionMemory>(),
                    scope2.ServiceProvider.GetRequiredService<RemoteResolutionMemory>());
        Assert.Single(services, d => d.ServiceType == typeof(IRemoteCommitResolver));
    }
}