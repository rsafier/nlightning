using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Gossip;

using Bitcoin.Gossip;
using Bitcoin.Wallet.Interfaces;
using Domain.Gossip.Interfaces;
using Domain.Onchain.Events;
using Domain.Onchain.Interfaces;

public class GossipBitcoinServiceCollectionExtensionsTests
{
    [Fact]
    public void Given_ChainAndWatcher_When_AddGossipBitcoinServices_Then_SingletonsResolve()
    {
        // Arrange
        var watcher = new Mock<IOutpointWatcher>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBitcoinChainService>(new FakeBitcoinChain());
        services.AddSingleton(watcher.Object);

        // Act
        services.AddGossipBitcoinServices();
        services.AddGossipBitcoinServices(); // idempotent
        using var provider = services.BuildServiceProvider();
        var lookup = provider.GetRequiredService<IFundingOutputLookup>();
        var verifier = provider.GetRequiredService<IGossipSignatureVerifier>();

        // Assert: one of each, and the lookup subscribed to disconnected blocks
        Assert.IsType<FundingOutputLookup>(lookup);
        Assert.IsType<GossipSignatureVerifier>(verifier);
        Assert.Same(lookup, provider.GetRequiredService<IFundingOutputLookup>());
        Assert.Single(services, d => d.ServiceType == typeof(IFundingOutputLookup));
        watcher.VerifyAdd(w => w.OnBlockDisconnected += It.IsAny<EventHandler<BlockDisconnectedEventArgs>>(),
                          Times.Once);
    }
}