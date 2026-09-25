using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Extensions;

using Daemon.Extensions;
using Daemon.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Node.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class NodeServiceExtensionsTests
{
    [Fact]
    public void Given_NodeServices_When_Composed_Then_EveryNodeServiceResolves()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddNltgNodeServices(BuildConfiguration(), new Mock<ISecureKeyManager>().Object);

        // BitcoinChainService connects to bitcoind in its constructor; keep the test hermetic
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Act / Assert: the services the Docker tests and the daemon both need come from the shared composition
        Assert.NotNull(provider.GetRequiredService<IChannelOpenValidator>());
        Assert.NotNull(provider.GetRequiredService<IChannelFactory>());
        Assert.NotNull(provider.GetRequiredService<ICommitmentTransactionModelFactory>());
        Assert.NotNull(provider.GetRequiredService<IFundingTransactionModelFactory>());
        Assert.NotNull(provider.GetRequiredService<ILightningSigner>());
        Assert.NotNull(provider.GetRequiredService<IFeeService>());
        Assert.NotNull(provider.GetRequiredService<IChannelManager>());
        Assert.NotNull(provider.GetRequiredService<IPeerManager>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<OpenChannelClientRequest,
                                 OpenChannelClientResponse>>());
        Assert.NotNull(scope.ServiceProvider
                            .GetRequiredService<IClientCommandHandler<OpenChannelClientSubscriptionRequest,
                                 OpenChannelClientSubscriptionResponse>>());
    }

    [Fact]
    public void Given_LayerServicesOnly_When_Composed_Then_ChannelFactoriesAndSignerResolve()
    {
        // Arrange: the layers' own DI, without anything the daemon registers by hand (NL-156)
        var services = new ServiceCollection();
        var configuration = BuildConfiguration();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IFeeService>().Object);
        services.AddLogging();
        services.AddOptions<Domain.Node.Options.NodeOptions>().BindConfiguration("Node");
        Application.DependencyInjection.AddApplicationServices(services);
        Infrastructure.Bitcoin.DependencyInjection.AddBitcoinInfrastructure(services);
        Infrastructure.DependencyInjection.AddInfrastructureServices(services);
        Infrastructure.Persistence.DependencyInjection.AddPersistenceInfrastructureServices(services, configuration);
        Infrastructure.Repositories.DependencyInjection.AddRepositoriesInfrastructureServices(services);
        Infrastructure.Serialization.DependencyInjection.AddSerializationInfrastructureServices(services);
        using var provider = services.BuildServiceProvider();

        // Act / Assert
        Assert.NotNull(provider.GetRequiredService<IChannelOpenValidator>());
        Assert.NotNull(provider.GetRequiredService<IChannelFactory>());
        Assert.NotNull(provider.GetRequiredService<ICommitmentTransactionModelFactory>());
        Assert.NotNull(provider.GetRequiredService<IFundingTransactionModelFactory>());
        Assert.NotNull(provider.GetRequiredService<ILightningSigner>());
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheSecureKeyManagerGivenIsUsed()
    {
        // Arrange
        var secureKeyManager = new Mock<ISecureKeyManager>().Object;
        var services = new ServiceCollection();

        // Act
        services.AddNltgNodeServices(BuildConfiguration(), secureKeyManager);
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Same(secureKeyManager, provider.GetRequiredService<ISecureKeyManager>());
    }

    private static IConfiguration BuildConfiguration()
    {
        return new ConfigurationBuilder()
              .AddInMemoryCollection([
                   new KeyValuePair<string, string?>("Node:Network", "regtest"),
                   new KeyValuePair<string, string?>("Database:Provider", "Sqlite"),
                   new KeyValuePair<string, string?>("Database:ConnectionString", "Data Source=:memory:")
               ])
              .Build();
    }
}