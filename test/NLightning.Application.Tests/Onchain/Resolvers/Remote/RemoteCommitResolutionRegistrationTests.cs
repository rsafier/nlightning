using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Onchain.Resolvers.Remote;

using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Remote;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class RemoteCommitResolutionRegistrationTests
{
    [Fact]
    public void Given_NodeServices_When_Registered_Then_OneSingletonOutputResolverForPeerCommitments()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(new Mock<ICommitmentOutputMapper>().Object);
        services.AddSingleton(new Mock<ISweepTransactionBuilder>().Object);
        services.AddSingleton(new Mock<ILightningSigner>().Object);
        services.AddSingleton(new Mock<IFeeService>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        services.AddScoped(_ => new Mock<IBitcoinWalletService>().Object);
        services.AddScoped(_ => new Mock<IUnitOfWork>().Object);

        // Act
        services.AddRemoteCommitResolutionServices();
        services.AddRemoteCommitResolutionServices();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var resolvers = provider.GetServices<IOutputResolver>().ToList();

        // Assert
        var resolver = Assert.IsType<RemoteCommitResolver>(Assert.Single(resolvers));
        Assert.Same(resolver, provider.GetRequiredService<RemoteCommitResolver>());
        Assert.IsType<WalletRemoteSweepDestination>(provider.GetRequiredService<IRemoteSweepDestination>());
        Assert.IsType<ChainRemoteCommitmentSource>(provider.GetRequiredService<IRemoteCommitmentSource>());
    }

    [Fact]
    public async Task Given_WalletDestination_When_ScriptAsked_Then_TheWalletIsUsedInAScopeOfItsOwn()
    {
        // Arrange (finding 5): the wallet may save new addresses; that save must not be the round's unit of work
        var wallet = new Mock<IBitcoinWalletService>();
        wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, false))
              .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 0, false,
                                                   "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080"));
        var scopes = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            scopes++;
            return wallet.Object;
        });
        await using var provider = services.BuildServiceProvider();
        var destination = new WalletRemoteSweepDestination(
            Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }),
            provider.GetRequiredService<IServiceScopeFactory>());

        // Act
        var first = await destination.GetScriptAsync(ChannelId.Zero, TestContext.Current.CancellationToken);
        var second = await destination.GetScriptAsync(ChannelId.Zero, TestContext.Current.CancellationToken);

        // Assert: a P2WPKH script, and one wallet service per call, each from a fresh scope
        Assert.Equal(22, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(2, scopes);
    }
}